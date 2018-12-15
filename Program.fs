
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


//TODO: https://github.com/SuaveIO/suave/issues/307

[<DataContract>]
type Msg2Printer =
   { 
      [<field: DataMember(Name = "printerID")>]
      printerID : string;
      [<field: DataMember(Name = "msg")>]
      msg : string;
   }

type Msg2PrinterFeed() =
   let event1 = new Event<Msg2Printer>()
   
   member this.Event1 = event1.Publish
   member this.TriggerEvent printerMsgPair =
      event1.Trigger printerMsgPair

         
let config = 
    let port = System.Environment.GetEnvironmentVariable("PORT")
    let ip127  = IPAddress.Parse("127.0.0.1")
    let ipZero = IPAddress.Parse("0.0.0.0")

    { defaultConfig with 
        bindings=[ (if port = null then HttpBinding.create HTTP ipZero (uint16 8083)  // 3 Nov - it was ipZero
                    else HttpBinding.create HTTP ipZero (uint16 port)) ] }

    
let ws allAgents (printJob:Msg2PrinterFeed) (jsonRequest:Msg2PrinterFeed) (webSocket : WebSocket) (context: HttpContext) =

  let (storeAgent:StoreAgent, printersAgent:PrintersAgent, logAgent:LogAgent) = allAgents
  let mutable printerUniqueId = ""
  let mutable channelName = ""

  let inbox = MailboxProcessor.Start (fun inbox -> async {
            let close = ref false
            while not !close do
                let! op, data, fi = inbox.Receive()
                if op=Binary then
                    do logAgent.AppendToLog (sprintf "%s (%s)> %s" (UTF8.toString data) channelName printerUniqueId)
                else
                    ()
                let! _ = webSocket.send op (data|> ByteSegment) fi
                close := op = Close                    
  })

    // https://github.com/SuaveIO/suave/issues/463
  async {
        let! successOrError = socket {

            // H15 error Heroku - https://devcenter.heroku.com/articles/error-codes#h15-idle-connection
            // A Pong frame MAY be sent unsolicited.  This serves as a unidirectional heartbeat.  
            // A response to an unsolicited Pong frame is not expected.
            let pongTimer = new System.Timers.Timer(float 20000)
            do pongTimer.AutoReset <- true
            let pongTimeoutEvent = pongTimer.Elapsed
            do pongTimeoutEvent |> Observable.subscribe (fun _ -> do inbox.Post (Pong, [||] , true)) |> ignore
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
                        do printersAgent.AddPrinter {uniqueID = printerUniqueId; productName = ""; appVersion = ""; friendlyName = ""; sgdSetAlertFeedback = "ifadLabelConversion"}
                        inbox.Post(Binary, UTF8.bytes """ { "open" : "v1.raw.zebra.com" } """, true)
                        inbox.Post(Binary, UTF8.bytes """ { "open" : "v1.config.zebra.com" } """, true)
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
                                        {printerID = printerUniqueId; msg= (buildpricetag prod)} |> printJob.TriggerEvent
                                    | None ->
                                        do logAgent.AppendToLog (sprintf "Barcode: %s not found in store" barcode)
                                | "ifadLabelConversion" ->  
                                    let label300dpi = (jsonalertval.GetProperty "setting_value").AsString()
                                    do logAgent.AppendToLog (sprintf "Original label: %s" label300dpi)    
                                    do logAgent.AppendToLog (sprintf "Converted label: %s" (convertIfadLabel label300dpi))       
                                    {printerID = printerUniqueId; msg = (convertIfadLabel label300dpi)} |> printJob.TriggerEvent
                                | "wikipediaConversion" ->  
                                    let demolabel = (jsonalertval.GetProperty "setting_value").AsString()
                                    do logAgent.AppendToLog (sprintf "Original label: %s" demolabel)    
                                    do logAgent.AppendToLog (sprintf "Converted label: %s" (convertWikipediaLabel demolabel))       
                                    {printerID = printerUniqueId; msg = (convertWikipediaLabel demolabel)} |> printJob.TriggerEvent
                                | _ -> ()
                            | None -> ()
                        | _ -> ()
                    | None -> ()

                    match jval.TryGetProperty "channel_name" with
                    | Some jsonval ->   
                        do channelName <- JsonExtensions.AsString (jsonval)
                        match jval.TryGetProperty "unique_id" with
                        | Some jsonval ->   do printerUniqueId <- JsonExtensions.AsString (jsonval)
                                            do logAgent.AppendToLog (sprintf "chan: %s printerID %s" channelName printerUniqueId)
                        | None -> ()
                        match channelName with
                        | "v1.raw.zebra.com" ->
                                let eventForThisChannel = Event.filter (fun pm -> pm.printerID=printerUniqueId) printJob.Event1
                                do eventForThisChannel |> Observable.subscribe (fun pm -> do inbox.Post(Binary, UTF8.bytes pm.msg , true)) |> ignore
                                // do printJob.TriggerEvent {printerID = channelUniqueId; msg = helloLabel() }
                        | "v1.config.zebra.com" ->     
                                let eventForThisChannel = Event.filter (fun pm -> pm.printerID=printerUniqueId) jsonRequest.Event1
                                do eventForThisChannel |> Observable.subscribe (fun pm -> do inbox.Post(Binary, UTF8.bytes pm.msg , true)) |> ignore
                                // do jsonRequest.TriggerEvent {printerID= channelUniqueId; msg= """{}{"device.configuration_number":null} """ }
                                do jsonRequest.TriggerEvent {printerID=printerUniqueId; msg= """{}{"alerts.configured":"ALL MESSAGES,SDK,Y,Y,WEBLINK.IP.CONN1,0,N,|SGD SET,SDK,Y,Y,WEBLINK.IP.CONN1,0,N,capture.channel1.data.raw"} """}
                                do jsonRequest.TriggerEvent {printerID=printerUniqueId; msg= """{}{"device.product_name":null} """ }
                                do jsonRequest.TriggerEvent {printerID=printerUniqueId; msg= """{}{"appl.name":null} """ }
                                do jsonRequest.TriggerEvent {printerID=printerUniqueId; msg= """{}{"capture.channel1.port":"usb"} """ }
                                do jsonRequest.TriggerEvent {printerID=printerUniqueId; msg= """{}{"capture.channel1.delimiter":"^XZ"} """ }
                                do jsonRequest.TriggerEvent {printerID=printerUniqueId; msg= """{}{"capture.channel1.max_length":"512"} """ }                                                        
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
                do inbox.Post (Pong, data, true)

              | (Close, _, _) ->
                do logAgent.AppendToLog "Got Close message from printer!"
                do inbox.Post (Close, [||], true)
        
                if printerUniqueId <> null then do (printersAgent.RemovePrinter printerUniqueId) else ()
                // after sending a Close message, stop the loop
                loop <- false
                do pongTimer.Stop()

              | (_,_,fi) -> 
                do logAgent.AppendToLog (sprintf "Unexpected message from printer of type %A" fi)
        }
        match successOrError with
        | Choice1Of2(con) -> ()
        | Choice2Of2(error) -> do logAgent.AppendToLog ("### ERROR in websocket monad ###")
        return successOrError
  }

[<DataContract>]
type ProductPrinterObj =
   { 
      [<field: DataMember(Name = "ProductObj")>]
      ProductObj : Product;
      [<field: DataMember(Name = "id")>]
      id : String;
   }

type LogEntryOrTimeout = Timeout | LogEntry of String

let app  : WebPart = 
  let logEvent = new Event<String>()
  let mLogAgent = new LogAgent(logEvent)
  
  let printJob = new Msg2PrinterFeed()
  let jsonRequest = new Msg2PrinterFeed()
  let storeAgent = new StoreAgent()
  let printersAgent = new PrintersAgent()
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
    path "/websocketWithSubprotocol" >=> 
          WebSocketUM.handShakeWithSubprotocol (chooseSubprotocol "v1.weblink.zebra.com") (ws allAgents printJob jsonRequest)
    path "/sseLog" >=> request (fun _ -> EventSource.handShake (fun out ->
          let emptyEvent = new Event<Unit>()
          let Timer15sec = new System.Timers.Timer(float 15000)
          do Timer15sec.AutoReset <- true
          let timeoutEvent = Timer15sec.Elapsed
          do Timer15sec.Start()
          let newEvent = (logEvent.Publish |> Event.map (fun str -> LogEntry str) , 
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
    ))

    GET >=> choose 
        [ path "/hello" >=> OK "Hello GET"
          path "/clearlog" >=> warbler (fun ctx -> OK ( mLogAgent.Empty(); "Log cleared" ))
          path "/logdump.json" >=> warbler (fun ctx -> OK ( mLogAgent.LogDump() ))
          path "/storepricelist.json" >=> warbler (fun ctx -> OK ( storeAgent.StoreInventory() )) 
          path "/printerslist.json" >=> warbler (fun ctx -> OK ( printersAgent.PrintersInventory() )) 
          browseHome 
        ]
    POST >=> choose
        [ path "/printerupdate" >=> 
           objectDo (fun prt -> printersAgent.AddPrinter prt
                                if prt.sgdSetAlertFeedback = "priceTag" then
                                    do jsonRequest.TriggerEvent {printerID=prt.uniqueID; msg= """{}{"capture.channel1.port":"bt"} """ }
                                    do jsonRequest.TriggerEvent {printerID=prt.uniqueID; msg= """{}{"capture.channel1.max_length":"64"} """ }
                                    do jsonRequest.TriggerEvent {printerID=prt.uniqueID; msg= """{}{"capture.channel1.delimiter":"\\015\\012"} """ }
                                else
                                    do jsonRequest.TriggerEvent {printerID=prt.uniqueID; msg= """{}{"capture.channel1.port":"usb"} """ }
                                    do jsonRequest.TriggerEvent {printerID=prt.uniqueID; msg= """{}{"capture.channel1.delimiter":"^XZ"} """ }
                                    do jsonRequest.TriggerEvent {printerID=prt.uniqueID; msg= """{}{"capture.channel1.max_length":"512"} """ }
                    )
          path "/productupdate" >=> objectDo (fun prod -> storeAgent.UpdateWith prod)
          path "/productremove" >=> objectDo (fun prod -> storeAgent.RemoveSku prod.sku)

          path "/json2printer" >=> objectDo (fun pm -> 
                                               do mLogAgent.AppendToLog (sprintf "POST /json2printer - %A" pm)
                                               do jsonRequest.TriggerEvent pm) 
          path "/printproduct" >=> objectDo (fun (prodprint:ProductPrinterObj) ->  
                                               do mLogAgent.AppendToLog (sprintf "POST /printproduct - %A" prodprint)
                                               do printJob.TriggerEvent {printerID=prodprint.id; msg =(buildpricetag prodprint.ProductObj)} )
          path "/printraw" >=> objectDo (fun (lblpr:Msg2Printer) ->  
                                               do mLogAgent.AppendToLog (sprintf "POST /printraw - %A" lblpr)
                                               do printJob.TriggerEvent lblpr )
        ]
    NOT_FOUND "Found no handlers." ]

//https://help.heroku.com/tickets/560930

[<EntryPoint>]
let main _ =
  startWebServer config app
  0
  