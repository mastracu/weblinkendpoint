
open FSharp.Data

open Suave
open Suave.Http
open Suave.Operators
open Suave.Filters
open Suave.Successful
open Suave.Files
open Suave.RequestErrors
open Suave.Logging
open Suave.Utils
open Suave.Json
open Suave.EventSource

open System
open System.Net
open System.Runtime.Serialization

open Suave.Sockets
open Suave.Sockets.Control
open Suave.WebSocketUM

open System.IO
open System.Xml
open System.Text

open tinyBase64Decoder
open StoreAgent
open MessageLogAgent
open PrintersAgent
open LabelBuilder
open System
open fw

let Crc16b (msg:byte[]) =
    let polynomial      = 0xA001us
    let mutable code    = 0xffffus
    for b in msg do
        code <- code ^^^ uint16 b
        for j in [0..7] do
            if (code &&& 1us <> 0us) then
                code <- (code >>> 1) ^^^ polynomial
            else
                code <- code >>> 1
    code

let sendBTCaptureCmds printerID (printersAgent:PrintersAgent) toLog= 
    do printersAgent.SendMsgOverConfigChannel printerID (Opcode.Binary, UTF8.bytes """{}{"capture.channel1.port":"bt"} """, true) toLog
    do printersAgent.SendMsgOverConfigChannel printerID (Opcode.Binary, UTF8.bytes """{}{"capture.channel1.max_length":"64"} """, true) toLog
    do printersAgent.SendMsgOverConfigChannel printerID (Opcode.Binary, UTF8.bytes """{}{"capture.channel1.delimiter":"\\015\\012"} """, true) toLog

let sendUSBCaptureCmds printerID (printersAgent:PrintersAgent) toLog= 
    do printersAgent.SendMsgOverConfigChannel printerID (Opcode.Binary, UTF8.bytes """{}{"capture.channel1.port":"usb"} """, true) toLog
    do printersAgent.SendMsgOverConfigChannel printerID (Opcode.Binary, UTF8.bytes """{}{"capture.channel1.delimiter":"^XZ"} """, true) toLog
    do printersAgent.SendMsgOverConfigChannel printerID (Opcode.Binary, UTF8.bytes """{}{"capture.channel1.max_length":"512"} """, true) toLog

//TODO: https://github.com/SuaveIO/suave/issues/307
        
let config = 
    let port = System.Environment.GetEnvironmentVariable("PORT")
    let ip127  = IPAddress.Parse("127.0.0.1")
    let ipZero = IPAddress.Parse("0.0.0.0")

    { defaultConfig with 
        bindings=[ (if port = null then HttpBinding.create HTTP ipZero (uint16 8083)  // 3 Nov - it was ipZero
                    else HttpBinding.create HTTP ipZero (uint16 port)) ] }

    
let ws allAgents (webSocket : WebSocket) (context: HttpContext) =

  let (storeAgent:StoreAgent, printersAgent:PrintersAgent, logAgent:LogAgent) = allAgents
  let mutable printerUniqueId = ""
  let mutable channelName = ""
  let mutable cbTimeoutEvent:IDisposable = null
  let mutable cbNewMessage2Send:IDisposable = null

  let pongTimer = new System.Timers.Timer(float 20000)
  do pongTimer.AutoReset <- true

  let inbox = MailboxProcessor.Start (fun inbox -> async {
        let close = ref false
        while not !close do
            let! (op, pld, fi), isLogged = inbox.Receive()
            do logAgent.AppendToLog (sprintf "pld crc16 = %u opCode = %A inbox queue: %d" (Crc16b pld) op inbox.CurrentQueueLength)
            if isLogged then
                do logAgent.AppendToLog (sprintf "%s (%s)> %s" (UTF8.toString pld) channelName printerUniqueId)
            else
                ()
            let! successOrError = webSocket.send op (pld|> ByteSegment) fi
            match successOrError with
            | Choice1Of2(con) -> ()
            | Choice2Of2(error) -> do logAgent.AppendToLog (sprintf "### ERROR %A in websocket send operation ###" error)

            close := op = Close                    
  })

  let releaseResources() = 
        do inbox.Post ((Opcode.Close, [||], true), false)        
        if (cbTimeoutEvent <> null) then
            cbTimeoutEvent.Dispose()
        else
            ()
        do pongTimer.Stop()
        do pongTimer.Dispose()
        if (cbNewMessage2Send <> null) then
            cbNewMessage2Send.Dispose()
        else
            ()
        match channelName with
        | "v1.raw.zebra.com" -> if printerUniqueId.Length>0 then do printersAgent.ClearRawChannel printerUniqueId (Some inbox) else ()
        | "v1.config.zebra.com" -> if printerUniqueId.Length>0 then do printersAgent.ClearConfigChannel printerUniqueId (Some inbox) else ()
        | "v1.main.zebra.com" -> if printerUniqueId.Length>0 then do printersAgent.RemovePrinter printerUniqueId inbox else ()
        | _ -> ()

  // https://github.com/SuaveIO/suave/issues/463
  async {
        let! successOrError = socket {

            // H15 error Heroku - https://devcenter.heroku.com/articles/error-codes#h15-idle-connection
            // A Pong frame MAY be sent unsolicited.  This serves as a unidirectional heartbeat.  
            // A response to an unsolicited Pong frame is not expected.
            let pongTimeoutEvent = pongTimer.Elapsed
            do cbTimeoutEvent <- pongTimeoutEvent |> Observable.subscribe (fun _ -> do inbox.Post ((Pong, [||] , true), false)) 
            do pongTimer.Start()

            // if `loop` is set to false, the server will stop receiving messages
            let mutable loop = true
            while loop do
              // the server will wait for a message to be received without blocking the thread
              let! msg = webSocket.read()

              match msg with
              // the message has type (Opcode * byte [] * bool)
              //
              // Opcode type:
              //   type Opcode = Continuation | Text | Binary | Reserved | Close | Ping | Pong
              //
              // byte [] contains the actual message
              //
              // the last element is the FIN byte, explained later
              //
              // The FIN byte:
              //
              // A single message can be sent separated by fragments. The FIN byte indicates the final fragment. Fragments
              //
              // As an example, this is valid code, and will send only one message to the client:
              //
              // do! webSocket.send Text firstPart false
              // do! webSocket.send Continuation secondPart false
              // do! webSocket.send Continuation thirdPart true
              //
              // More information on the WebSocket protocol can be found at: https://tools.ietf.org/html/rfc6455#page-34
              //

              | (Binary, data, true) ->
                // the message can be converted to a string
                // let str = UTF8.toString data
                let str = Encoding.ASCII.GetString(data)
                let msglen = data.Length
                let response = sprintf "%s <(%s) %s (bytes = %d)" str channelName printerUniqueId msglen
                do logAgent.AppendToLog response
                if (not (channelName="v1.raw.zebra.com")) then                 
                    let jval = JsonValue.Parse str
                    match jval.TryGetProperty "discovery_b64" with
                    | Some jsonval ->   
                        let zebraDiscoveryPacket = JsonExtensions.AsString jsonval |> decode64
                        let uniqueID = List.rev (snd (List.fold (fun (pos,acclist) byte -> (pos+1, if (pos > 187 && pos < 202 ) then byte::acclist else acclist))  (0,[]) zebraDiscoveryPacket))
                        do printerUniqueId <- uniqueID |> intListToString
                        do logAgent.AppendToLog (sprintf "discovery_b64 property printerID: %s"  printerUniqueId)
                        do printerUniqueId <- printerUniqueId.Substring (0, (printerUniqueId.IndexOf 'J' + 10))
                        do logAgent.AppendToLog (sprintf "adjusted printerID: %s"  printerUniqueId)
                        do channelName <- "v1.main.zebra.com"
                        do printersAgent.AddPrinter printerUniqueId inbox
                        do printersAgent.SendMsgOverMainChannel printerUniqueId (Opcode.Binary, UTF8.bytes """ { "open" : "v1.raw.zebra.com" } """, true) true
                        do printersAgent.SendMsgOverMainChannel printerUniqueId (Opcode.Binary, UTF8.bytes """ { "open" : "v1.config.zebra.com" } """, true) true
                    | None -> ()

                    match jval.TryGetProperty "alert" with
                    | Some jsonalertval ->   
                        match (jsonalertval.GetProperty "condition_id").AsString() with
                        | "SGD SET" -> 
                            let sgdFeedback = printersAgent.FetchPrinterInfo printerUniqueId
                            match sgdFeedback with 
                            | Some feedback -> 
                                do logAgent.AppendToLog (sprintf "Printer application: %s" feedback.sgdSetAlertFeedback )
                                match feedback.sgdSetAlertFeedback with
                                | "priceTag" -> 
                                    let barcode = (jsonalertval.GetProperty "setting_value").AsString()
                                    let maybeProd = storeAgent.EanLookup barcode
                                    match maybeProd with
                                    | Some prod -> 
                                        let priceString = prod.unitPrice.ToString()
                                        do logAgent.AppendToLog (sprintf "Barcode: %s Price: %s Description: %s" barcode priceString prod.description) 
                                        let priceLbl = (buildpricetag prod)
                                        do printersAgent.SendMsgOverRawChannel printerUniqueId (Opcode.Binary, UTF8.bytes priceLbl, true) true
                                    | None ->
                                        do logAgent.AppendToLog (sprintf "Barcode: %s not found in store" barcode)
                                | "ifadLabelConversion" ->  
                                    let label300dpi = (jsonalertval.GetProperty "setting_value").AsString()
                                    let label200dpi = convertIfadLabel label300dpi
                                    do logAgent.AppendToLog (sprintf "Original label: %s" label300dpi)    
                                    do logAgent.AppendToLog (sprintf "Converted label: %s" label200dpi)
                                    do printersAgent.SendMsgOverRawChannel printerUniqueId (Opcode.Binary, UTF8.bytes label200dpi, true) true
                                | "wikipediaConversion" ->  
                                    let demoinlabel = (jsonalertval.GetProperty "setting_value").AsString()
                                    let demooutlabel = (convertWikipediaLabel demoinlabel)
                                    do logAgent.AppendToLog (sprintf "Original label: %s" demoinlabel)    
                                    do logAgent.AppendToLog (sprintf "Converted label: %s" demooutlabel)
                                    do printersAgent.SendMsgOverRawChannel printerUniqueId (Opcode.Binary, UTF8.bytes demooutlabel, true) true
                                | _ -> ()
                            | None -> ()
                        | _ -> ()
                    | None -> ()

                    match jval.TryGetProperty "channel_name" with
                    | Some jsonval ->   
                        do channelName <- JsonExtensions.AsString (jsonval)
                        match jval.TryGetProperty "unique_id" with
                        | Some jsonval ->   
                             do printerUniqueId <- JsonExtensions.AsString (jsonval)
                             do logAgent.AppendToLog (sprintf "chan: %s printerID %s" channelName printerUniqueId)
                        | None -> ()
                        match channelName with
                        | "v1.raw.zebra.com" ->
                             // let helloLbl = helloLabel()
                             // do printersAgent.SendMsgOverRawChannel printerUniqueId (Opcode.Binary, Encoding.ASCII.GetBytes helloLbl, true) true
                             do printersAgent.UpdateRawChannel printerUniqueId (Some inbox)
                        | "v1.config.zebra.com" ->     
                             do printersAgent.UpdateConfigChannel printerUniqueId (Some inbox)
                             do printersAgent.SendMsgOverConfigChannel printerUniqueId (Opcode.Binary, UTF8.bytes """{}{"alerts.configured":"ALL MESSAGES,SDK,Y,Y,WEBLINK.IP.CONN1,0,N,|SGD SET,SDK,Y,Y,WEBLINK.IP.CONN1,0,N,capture.channel1.data.raw"} """, true) true
                             do printersAgent.SendMsgOverConfigChannel printerUniqueId (Opcode.Binary, UTF8.bytes """{}{"device.product_name":null} """, true) true
                             do printersAgent.SendMsgOverConfigChannel printerUniqueId (Opcode.Binary, UTF8.bytes """{}{"appl.name":null} """, true) true
                             do sendUSBCaptureCmds printerUniqueId printersAgent true
                        | _ -> ()
                    | None -> ()

                    match jval.TryGetProperty "device.product_name" with    
                    // match jval.TryGetProperty "device.configuration_number" with
                    | Some jsonval ->   let devConfigNumber = JsonExtensions.AsString (jsonval)
                                        do printersAgent.UpdatePartNumber printerUniqueId devConfigNumber
                    | None -> ()

                    match jval.TryGetProperty "appl.name" with
                    | Some jsonval ->   let applName = JsonExtensions.AsString (jsonval)
                                        do printersAgent.UpdateAppVersion printerUniqueId applName
                    | None -> ()

              | (Ping, data, true) ->
                // Ping message received. Responding with Pong
                // The printer sends a PING message roughly ever 60 seconds. The server needs to respond with a PONG, per RFC6455
                // After three failed PING attempts, the printer disconnects and attempts to reconnect

                do logAgent.AppendToLog "Ping message from printer. Responding with Pong message"
                // A Pong frame sent in response to a Ping frame must have identical "Application data" as found in the message body of the Ping frame being replied to.
                // the `send` function sends a message back to the client
                do inbox.Post ((Opcode.Pong, data, true), false)

              | (Close, _, _) ->
                do logAgent.AppendToLog (sprintf "(%s, %s) Got Close message from printer, releasing resources" printerUniqueId channelName)
                do releaseResources()
                loop <- false
        
              | (_,_,fi) -> 
                do logAgent.AppendToLog (sprintf "Unexpected message from printer of type %A" fi)
        }
        match successOrError with
        | Choice1Of2(con) -> ()
        | Choice2Of2(error) -> 
            do logAgent.AppendToLog (sprintf "### (%s, %s) ERROR in websocket monad, releasing resources ###" printerUniqueId channelName)
            do releaseResources()
        return successOrError
  }



type LogEntryOrTimeout = Timeout | LogEntry of String

let sseContinuation sEvent (mLogAgent:LogAgent) = (fun out ->
          let emptyEvent = new Event<Unit>()
          let Timer15sec = new System.Timers.Timer(float 15000)
          do Timer15sec.AutoReset <- true
          let timeoutEvent = Timer15sec.Elapsed
          do Timer15sec.Start()
          let newEvent = (sEvent |> Event.map (fun str -> LogEntry str) , 
                          timeoutEvent |> Event.map (fun _ -> Timeout)) ||> Event.merge

          let inbox = MailboxProcessor.Start (fun inbox -> 
                let rec loop n = async {   
                        let! newEvent = inbox.Receive()
                        match newEvent with
                        | LogEntry str -> 
                            let! _ = string n |> esId out
                            let newLogEntryLines = str.Split '\n'
                            for line in newLogEntryLines do
                                let! _ = line |> data out
                                ()
                        | Timeout ->
                            let! _ = "keepAlive" |> comment out
                            ()
                        let! _ = dispatch out
                        return! loop (n+1) }
                loop 0)

          let disposableResource = newEvent |> Observable.subscribe (fun arg -> do inbox.Post(arg)) 

          // https://github.com/SuaveIO/suave/issues/463
          async {
                let! successOrError = socket {
                      let! never=Control.Async.AwaitEvent(emptyEvent.Publish) |>  Suave.Sockets.SocketOp.ofAsync
                      return out
                }
                match successOrError with
                | Choice1Of2(con) -> ()
                | Choice2Of2(error) -> 
                    disposableResource.Dispose()
                    Timer15sec.Stop()
                    Timer15sec.Dispose()
                    do mLogAgent.AppendToLog ("Forced SSE disconnect - disposed resources in sse handshake continuation function")
                return successOrError
          }
)

[<DataContract>]
type ProductPrinterObj =
   { 
      [<field: DataMember(Name = "ProductObj")>]
      ProductObj : Product;
      [<field: DataMember(Name = "id")>]
      id : String;
   }

[<DataContract>]
type Msg2Printer =
   {
      [<field: DataMember(Name = "printerID")>]
      printerID : string;
      [<field: DataMember(Name = "msg")>]
      msg : string;
   }

let app  : WebPart = 
  let logEvent = new Event<String>()
  let mLogAgent = new LogAgent(logEvent)
  
  let storeAgent = new StoreAgent()
  let printersAgent = new PrintersAgent(mLogAgent)
  let allAgents = (storeAgent, printersAgent, mLogAgent)
  
  let objectDo func:WebPart = 
     mapJson (fun obj -> 
                       func obj
                       obj)

  do mLogAgent.AppendToLog "WebServer started"

  //let appname = System.Environment.GetEnvironmentVariable("HEROKU_APP_NAME")
  //let releaseAt = System.Environment.GetEnvironmentVariable("HEROKU_RELEASE_CREATED_AT")
  //let releaseVersion = System.Environment.GetEnvironmentVariable("HEROKU_RELEASE_VERSION")

  choose [
    path "/websocketWithSubprotocol" >=> WebSocketUM.handShakeWithSubprotocol (chooseSubprotocol "v1.weblink.zebra.com") (ws allAgents)
    path "/sseLog" >=> request (fun _ -> EventSource.handShake (sseContinuation logEvent.Publish mLogAgent ))

    GET >=> choose 
        [ path "/hello" >=> OK "Hello GET"
          path "/clearlog" >=> warbler (fun ctx -> OK ( mLogAgent.Empty(); "Log cleared" ))
          path "/logdump.json" >=> warbler (fun ctx -> OK ( mLogAgent.LogDump() ))
          path "/storepricelist.json" >=> warbler (fun ctx -> OK ( storeAgent.StoreInventory() )) 
          path "/fwlist.json" >=> warbler (fun ctx -> OK ( fw.fwFileList() )) 
          path "/printerslist.json" >=> warbler (fun ctx -> OK ( printersAgent.PrintersInventory() )) 
          browseHome 
        ]
    POST >=> choose
        [ path "/printerupdate" >=> 
           objectDo (fun prt -> printersAgent.UpdateApp prt.uniqueID prt.sgdSetAlertFeedback
                                if prt.sgdSetAlertFeedback = "priceTag" then
                                    sendBTCaptureCmds prt.uniqueID printersAgent true
                                else
                                    sendUSBCaptureCmds prt.uniqueID printersAgent true
                    )
          path "/productupdate" >=> objectDo (fun prod -> storeAgent.UpdateWith prod)
          path "/productremove" >=> objectDo (fun prod -> storeAgent.RemoveSku prod.sku)

          path "/json2printer" >=> objectDo (fun (pm:Msg2Printer) -> 
                                               do mLogAgent.AppendToLog (sprintf "POST /json2printer - %A" pm)
                                               let data2send = UTF8.bytes (pm.msg)
                                               do printersAgent.SendMsgOverConfigChannel pm.printerID (Opcode.Binary, data2send, true ) true)
          path "/printproduct" >=> objectDo (fun (prodprint:ProductPrinterObj) ->  
                                               do mLogAgent.AppendToLog (sprintf "POST /printproduct - %A" prodprint)
                                               let data2send = UTF8.bytes (buildpricetag prodprint.ProductObj)
                                               do printersAgent.SendMsgOverRawChannel prodprint.id (Opcode.Binary, data2send, true ) true) 
          path "/upgradeprinter" >=> objectDo (fun (fwjob:FwJobObj) ->  
                                               do mLogAgent.AppendToLog (sprintf "POST /upgradeprinter - %A" fwjob)
                                               do match printersAgent.FetchPrinterInfo fwjob.id with
                                                  | None -> ()
                                                  | Some pr -> match pr.rawChannelAgent with
                                                               | None -> ()
                                                               | Some agent -> do doFwUpgrade fwjob agent mLogAgent)
          path "/printraw" >=> objectDo (fun (pm:Msg2Printer) ->  
                                               do mLogAgent.AppendToLog (sprintf "POST /printraw - %A" pm)
                                               let data2send = UTF8.bytes (pm.msg)
                                               do printersAgent.SendMsgOverRawChannel pm.printerID (Opcode.Binary, data2send, true ) true) 
        ]
    NOT_FOUND "Found no handlers." ]

//https://help.heroku.com/tickets/560930

[<EntryPoint>]
let main _ =
  startWebServer config app
  0
  