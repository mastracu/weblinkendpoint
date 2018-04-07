
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

let helloLabel = "
CT~~CD,~CC^~CT~
^XA~TA000~JSN^LT0^MNW^MTT^PON^PMN^LH0,0^JMA^PR3,3~SD17^JUS^LRN^CI0^XZ
^XA
^MMC
^PW609
^LL0811
^LS0
^FT373,536^A0I,28,28^FH\^FDSUAVE F# APP CONNECTED !^FS
^PQ1,1,1,Y^XZ"

let buildpricetag (prod:Product) =
    let label0 = "
        ^XA
        ^MMT
        ^PW609
        ^LL0240
        ^LS0
        ^FT176,49^A0N,28,50^FB236,1,0,C^FH\^FDZebra Store^FS
        ^BY3,3,41^FT156,210^BCN,,Y,N
        ^FD>;BBBBBBBBBBBBB^FS
        ^FT280,148^A0N,28,28^FH\^FDPPPPPP\15 al pezzo^FS
        ^FT189,148^A0N,28,28^FH\^FDPrezzo:^FS
        ^FT270,111^A0N,28,28^FH\^FDXXXXXXXXXXXX^FS
        ^FT157,111^A0N,28,28^FH\^FDProdotto:^FS
        ^PQ1,0,1,Y^XZ
        "
    let label1 = String.replace "PPPPPP" (prod.unitPrice.ToString()) label0
    let label2 = String.replace "BBBBBBBBBBBBB" prod.eanCode label1
    String.replace "XXXXXXXXXXXX" prod.description label2

//TODO: https://github.com/SuaveIO/suave/issues/307

type PrintEventClass() =
   let event1 = new Event<String>()
   
   member this.Event1 = event1.Publish
   member this.TriggerEvent (str) =
      event1.Trigger str

         
let config = 
    let port = System.Environment.GetEnvironmentVariable("PORT")
    let ip127  = IPAddress.Parse("127.0.0.1")
    let ipZero = IPAddress.Parse("0.0.0.0")

    { defaultConfig with 
        bindings=[ (if port = null then HttpBinding.create HTTP ip127 (uint16 8080)
                    else HttpBinding.create HTTP ipZero (uint16 port)) ] }


let ws (logAgent:LogAgent) (evt2Printer:PrintEventClass) (storeAgent:StoreAgent) (webSocket : WebSocket) (context: HttpContext) =

  let inbox = MailboxProcessor.Start (fun inbox -> async {
            let close = ref false
            while not !close do
                let! op, data, fi = inbox.Receive()
                // do logAgent.UpdateWith (sprintf "Sending message to printer of type %A" op)
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

    do logAgent.AppendToLog "About to enter websocket read loop"

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
        | Some jsonval ->   let zebraDiscoveryPacket = JsonExtensions.AsString jsonval |> decode64
                            //let offsetUniqueID = zebraDiscoveryPacket.LastIndexOf "50J163700136"
                            do logAgent.AppendToLog (sprintf "discovery_b64 property decode length: %d" zebraDiscoveryPacket.Length)
                            let uniqueID = snd (List.foldBack (fun byte (pos,acclist) -> (pos+1, if (pos > 60 && pos < 130 ) then byte::acclist else acclist)) zebraDiscoveryPacket (0,[]))
                            do logAgent.AppendToLog (sprintf "discovery_b64 property received on main channel: b64 encoded unique_id: %s"  (uniqueID |> intListToString))
                            inbox.Post(Binary, UTF8.bytes """ { "configure_alert" : "ALL MESSAGES,SDK,Y,Y,,,N,|SGD SET,SDK,Y,Y,,,N,capture.channel1.data.raw" } """, true)
                            inbox.Post(Binary, UTF8.bytes """ { "open" : "v1.raw.zebra.com" } """, true)
        | None -> () 

        match jval.TryGetProperty "channel_name" with
        | Some jsonval ->   let chanid = JsonExtensions.AsString (jsonval)
                            do logAgent.AppendToLog (sprintf "Channel name: %s" chanid)
                            if chanid = "v1.raw.zebra.com" then
                               // request friendly name of printer and associate it to the raw channel
                               // https://support.zebra.com/cpws/docs/zpl/device_friendly_name.pdf
                               do evt2Printer.Event1 |> Observable.subscribe (fun lbl -> do logAgent.AppendToLog (sprintf "Printing request")
                                                                                         inbox.Post(Binary, UTF8.bytes lbl , true)) |> ignore
                               do evt2Printer.TriggerEvent(helloLabel)
                            else 
                               ()
        | None -> ()

        match jval.TryGetProperty "alert" with
        | Some jsonalertval ->   match (jsonalertval.GetProperty "condition_id").AsString() with
                                 | "SGD SET" -> let barcode = (jsonalertval.GetProperty "setting_value").AsString()
                                                let maybeProd = storeAgent.EanLookup barcode
                                                match maybeProd with
                                                | Some prod -> 
                                                   let priceString = prod.unitPrice.ToString()
                                                   do logAgent.AppendToLog (sprintf "Barcode: %s Price: %s Description: %s" barcode priceString prod.description)       
                                                   prod |> (buildpricetag >> evt2Printer.TriggerEvent)
                                                | None ->
                                                   do logAgent.AppendToLog (sprintf "Barcode: %s not found in store" barcode)
                                 | _ -> ()
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

        // after sending a Close message, stop the loop
        loop <- false
        do pongTimer.Stop()

      | (_,_,fi) -> 
        do logAgent.AppendToLog (sprintf "Unexpected message from printer of type %A" fi)
    
 }


let app  : WebPart = 
  let mLogAgent = new LogAgent()
  let evtPrint = new PrintEventClass()
  let storeAgent = new StoreAgent()
  let toSendtoPrinter = evtPrint.Event1
  
  let productDo func:WebPart = 
     mapJson (fun (prod:Product) -> 
                       func prod
                       prod)

  do mLogAgent.AppendToLog "WebServer started"
  choose [
    path "/websocketWithSubprotocol" >=> handShakeWithSubprotocol (chooseSubprotocol "v1.weblink.zebra.com") (ws mLogAgent evtPrint storeAgent)
    GET >=> choose 
        [ path "/hello" >=> OK "Hello GET"
          path "/hellolabel" >=>  warbler (fun ctx -> evtPrint.TriggerEvent(helloLabel); OK ("Triggered"))
          path "/clearlog" >=> warbler (fun ctx -> OK ( mLogAgent.Empty(); "Log cleared" ))
          path "/logdump.json" >=> warbler (fun ctx -> OK ( mLogAgent.LogDump() ))
          path "/storepricelist.json" >=> warbler (fun ctx -> OK ( storeAgent.StoreInventory() )) 
          // TODO: add "/connectedprinters.json"
          browseHome ]
    POST >=> choose
        [ path "/productupdate" >=> productDo (fun prod -> do mLogAgent.AppendToLog "productupdate received"
                                                           storeAgent.UpdateWith prod)
          path "/printproduct" >=> productDo (buildpricetag >> evtPrint.TriggerEvent) ]
          // TODO: add "/printlabel" evtPrint.Trigger(body of POST) - event type is  printerid,label pair to support multiple printer connections 
    NOT_FOUND "Found no handlers." ]

//https://help.heroku.com/tickets/560930

[<EntryPoint>]
let main _ =
  startWebServer config app
  0

