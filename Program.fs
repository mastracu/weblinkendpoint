
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
        bindings=[ (if port = null then HttpBinding.create HTTP ip127 (uint16 8080)
                    else HttpBinding.create HTTP ipZero (uint16 port)) ] }


let ws allAgents (printJob:Msg2PrinterFeed) (jsonRequest:Msg2PrinterFeed) (webSocket : WebSocket) (context: HttpContext) =

  let (storeAgent:StoreAgent, printersAgent:PrintersAgent, logAgent:LogAgent) = allAgents

  let inbox = MailboxProcessor.Start (fun inbox -> async {
            let close = ref false
            while not !close do
                let! op, data, fi = inbox.Receive()
                if op=Binary then
                    do logAgent.AppendToLog (sprintf "Sending message: %s" (UTF8.toString data))
                else
                    ()
                let! _ = webSocket.send op (data|> ByteSegment) fi
                close := op = Close                    
        })

  socket {

    // H15 error Heroku - https://devcenter.heroku.com/articles/error-codes#h15-idle-connection
    // A Pong frame MAY be sent unsolicited.  This serves as a unidirectional heartbeat.  
    // A response to an unsolicited Pong frame is not expected.
    let pongTimer = new System.Timers.Timer(float 20000)
    do pongTimer.AutoReset <- true
    let pongTimeoutEvent = pongTimer.Elapsed
    do pongTimeoutEvent |> Observable.subscribe (fun _ -> do inbox.Post (Pong, [||] , true)) |> ignore
    do pongTimer.Start()

    let mutable channelUniqueId = ""

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
        let str = UTF8.toString data
        let response = sprintf "Binary message from printer: %s" str
        do logAgent.AppendToLog response
        let jval = JsonValue.Parse str

        match jval.TryGetProperty "discovery_b64" with
        | Some jsonval ->   
                            let zebraDiscoveryPacket = JsonExtensions.AsString jsonval |> decode64
                            let uniqueID = List.rev (snd (List.fold (fun (pos,acclist) byte -> (pos+1, if (pos > 187 && pos < 202 ) then byte::acclist else acclist))  (0,[]) zebraDiscoveryPacket))
                            do channelUniqueId <- uniqueID |> intListToString
                            do logAgent.AppendToLog (sprintf "discovery_b64 property received on main channel unique_id: %s"  channelUniqueId)
                            do channelUniqueId <- channelUniqueId.Substring (0, (channelUniqueId.IndexOf 'J' + 10))
                            do logAgent.AppendToLog (sprintf "adjusted unique_id: %s"  channelUniqueId)
                            do printersAgent.AddPrinter {uniqueID = channelUniqueId; productName = ""; appVersion = ""; friendlyName = ""; sgdSetAlertFeedback = "IfadLabelConversion"}
                            // inbox.Post(Binary, UTF8.bytes """ { "configure_alert" : "ALL MESSAGES,SDK,Y,Y,,,N,|SGD SET,SDK,Y,Y,,,N,capture.channel1.data.raw" } """, true)
                            // inbox.Post(Binary, UTF8.bytes """ { "configure_alert" : "ALL MESSAGES,SDK,Y,Y,WEBLINK.IP.CONN1,0,N,|SGD SET,SDK,Y,Y,WEBLINK.IP.CONN1,0,N,capture.channel1.data.raw" } """, true)
                            inbox.Post(Binary, UTF8.bytes """ { "open" : "v1.raw.zebra.com" } """, true)
                            inbox.Post(Binary, UTF8.bytes """ { "open" : "v1.config.zebra.com" } """, true)
        | None -> () 

        match jval.TryGetProperty "alert" with
        | Some jsonalertval ->   
            match (jsonalertval.GetProperty "condition_id").AsString() with
            | "SGD SET" -> 
                let sgdFeedback = printersAgent.FetchPrinterInfo channelUniqueId
                match sgdFeedback with 
                | Some feedback -> 
                    match feedback.sgdSetAlertFeedback with
                    | "PriceTag" -> 
                        let barcode = (jsonalertval.GetProperty "setting_value").AsString()
                        let maybeProd = storeAgent.EanLookup barcode
                        match maybeProd with
                        | Some prod -> 
                            let priceString = prod.unitPrice.ToString()
                            do logAgent.AppendToLog (sprintf "Barcode: %s Price: %s Description: %s" barcode priceString prod.description)       
                            {printerID = channelUniqueId; msg= (buildpricetag prod)} |> printJob.TriggerEvent
                        | None ->
                            do logAgent.AppendToLog (sprintf "Barcode: %s not found in store" barcode)
                    | "IfadLabelConversion" ->  
                        let label300dpi = (jsonalertval.GetProperty "setting_value").AsString()
                        do logAgent.AppendToLog (sprintf "Input label: %s" label300dpi)    
                        do logAgent.AppendToLog (sprintf "New label: %s" (convertIfadLabel label300dpi))       
                        {printerID = channelUniqueId; msg = (convertIfadLabel label300dpi)} |> printJob.TriggerEvent
                    | _ -> ()
                | None -> ()
            | _ -> ()
        | None -> ()

        match jval.TryGetProperty "channel_name" with
        | Some jsonval ->   let channelName = JsonExtensions.AsString (jsonval)
                            do logAgent.AppendToLog (sprintf "Channel name: %s" channelName)
                            match channelName with
                            | "v1.raw.zebra.com" -> 
                                    match jval.TryGetProperty "unique_id" with
                                    | Some jsonval ->   do channelUniqueId <- JsonExtensions.AsString (jsonval)
                                                        let eventForThisChannel = Event.filter (fun pm -> pm.printerID=channelUniqueId) printJob.Event1
                                                        do eventForThisChannel |> Observable.subscribe (fun pm -> do inbox.Post(Binary, UTF8.bytes pm.msg , true)) |> ignore
                                                        do printJob.TriggerEvent {printerID = channelUniqueId; msg = helloLabel() }
                                    | None -> ()
                            | "v1.config.zebra.com" -> 
                                    match jval.TryGetProperty "unique_id" with
                                    | Some jsonval ->   do channelUniqueId <- JsonExtensions.AsString (jsonval)
                                                        let eventForThisChannel = Event.filter (fun pm -> pm.printerID=channelUniqueId) jsonRequest.Event1
                                                        do eventForThisChannel |> Observable.subscribe (fun pm -> do inbox.Post(Binary, UTF8.bytes pm.msg , true)) |> ignore
                                                        // do jsonRequest.TriggerEvent {printerID= channelUniqueId; msg= """{}{"device.configuration_number":null} """ }
                                                        do jsonRequest.TriggerEvent {printerID=channelUniqueId; msg= """{}{"alerts.configured":"ALL MESSAGES,SDK,Y,Y,WEBLINK.IP.CONN1,0,N,|SGD SET,SDK,Y,Y,WEBLINK.IP.CONN1,0,N,capture.channel1.data.raw"} """}
                                                        do jsonRequest.TriggerEvent {printerID= channelUniqueId; msg= """{}{"device.product_name":null} """ }
                                                        do jsonRequest.TriggerEvent {printerID= channelUniqueId; msg= """{}{"appl.name":null} """ }
                                                        do jsonRequest.TriggerEvent {printerID=channelUniqueId; msg= """{}{"capture.channel1.port":"usb"} """ }
                                                        do jsonRequest.TriggerEvent {printerID=channelUniqueId; msg= """{}{"capture.channel1.delimiter":"^XZ"} """ }
                                                        do jsonRequest.TriggerEvent {printerID=channelUniqueId; msg= """{}{"capture.channel1.max_length":"512"} """ }
                                                        


                                    | None -> ()
                            | _ -> ()
        | None -> ()

        match jval.TryGetProperty "device.product_name" with
        // match jval.TryGetProperty "device.configuration_number" with
        | Some jsonval ->   let devConfigNumber = JsonExtensions.AsString (jsonval)
                            do printersAgent.UpdatePartNumber channelUniqueId devConfigNumber
        | None -> ()

        match jval.TryGetProperty "appl.name" with
        | Some jsonval ->   let applName = JsonExtensions.AsString (jsonval)
                            do printersAgent.UpdateAppVersion channelUniqueId applName
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
        
        if channelUniqueId <> null then do (printersAgent.RemovePrinter channelUniqueId) else ()
        // after sending a Close message, stop the loop
        loop <- false
        do pongTimer.Stop()

      | (_,_,fi) -> 
        do logAgent.AppendToLog (sprintf "Unexpected message from printer of type %A" fi)
    
 }

[<DataContract>]
type ProductPrinterObj =
   { 
      [<field: DataMember(Name = "ProductObj")>]
      ProductObj : Product;
      [<field: DataMember(Name = "id")>]
      id : String;
   }

let app  : WebPart = 
  let mLogAgent = new LogAgent()
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

  let appname = System.Environment.GetEnvironmentVariable("HEROKU_APP_NAME")
  let releaseAt = System.Environment.GetEnvironmentVariable("HEROKU_RELEASE_CREATED_AT")
  let releaseVersion = System.Environment.GetEnvironmentVariable("HEROKU_RELEASE_VERSION")

  if appname <> null then
     do mLogAgent.AppendToLog (sprintf "Heroku appname: %s" appname)
     do mLogAgent.AppendToLog (sprintf "Heroku app released at: %s" releaseAt)
     do mLogAgent.AppendToLog (sprintf "Heroku app release version: %s" releaseVersion)
  else
     // doesn't run on Heroku
     ()

  choose [
    path "/websocketWithSubprotocol" >=> handShakeWithSubprotocol (chooseSubprotocol "v1.weblink.zebra.com") (ws allAgents printJob jsonRequest)
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
                                if prt.sgdSetAlertFeedback = "PriceTag" then
                                    do jsonRequest.TriggerEvent {printerID=prt.uniqueID; msg= """{}{"capture.channel1.port":"bt"} """ }
                                    do jsonRequest.TriggerEvent {printerID=prt.uniqueID; msg= """{}{"capture.channel1.max_length":"64"} """ }
                                    do jsonRequest.TriggerEvent {printerID=prt.uniqueID; msg= """{}{"capture.channel1.delimiter":"\\015\\012"} """ }
                                else
                                    do jsonRequest.TriggerEvent {printerID=prt.uniqueID; msg= """{}{"capture.channel1.port":"usb"} """ }
                                    do jsonRequest.TriggerEvent {printerID=prt.uniqueID; msg= """{}{"capture.channel1.delimiter":"^XZ"} """ }
                                    do jsonRequest.TriggerEvent {printerID=prt.uniqueID; msg= """{}{"capture.channel1.max_length":"512"} """ }
                    )
          path "/productupdate" >=> objectDo (fun prod -> storeAgent.UpdateWith prod)
          path "/json2printer" >=> objectDo (fun pm -> jsonRequest.TriggerEvent pm) 
          path "/productremove" >=> objectDo (fun prod -> storeAgent.RemoveSku prod.sku)
          path "/printproduct" >=> objectDo (fun (prodprint:ProductPrinterObj) ->  do mLogAgent.AppendToLog (sprintf "printproduct. id: %s prod: %A" prodprint.id prodprint.ProductObj)
                                                                                   do printJob.TriggerEvent {printerID=prodprint.id; msg =(buildpricetag prodprint.ProductObj)} )         ]
    NOT_FOUND "Found no handlers." ]

//https://help.heroku.com/tickets/560930

[<EntryPoint>]
let main _ =
  startWebServer config app
  0
  