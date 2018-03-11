
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


let hellolabel = "
CT~~CD,~CC^~CT~
^XA~TA000~JSN^LT0^MNW^MTT^PON^PMN^LH0,0^JMA^PR3,3~SD17^JUS^LRN^CI0^XZ
^XA
^MMC
^PW609
^LL0811
^LS0
^FT373,536^A0I,28,28^FH\^FDHeroku-hosted SUAVE-F# APP^FS
^PQ1,1,1,Y^XZ"


//TODO: https://github.com/SuaveIO/suave/issues/307
   
type DumpAgentMsg = 
    | Exit
    | Reset
    | UpdateWith of String
    | DumpDevicesState  of AsyncReplyChannel<String>

type MessageDump = 
   { DebugLog : String list } 
   static member Empty = {DebugLog = [] }
   member x.UpdateWith (msg:String) = 
      { DebugLog = (string DateTime.Now + " " + msg) :: x.DebugLog }

type PrinterMsgAgent() =
    let printerMsgMailboxProcessor =
        MailboxProcessor.Start(fun inbox ->
            let rec locationAgentLoop msgDump =
                async { let! msg = inbox.Receive()
                        match msg with
                        | Exit -> return ()
                        | Reset -> return! locationAgentLoop MessageDump.Empty
                        | UpdateWith newMsg -> return! locationAgentLoop (msgDump.UpdateWith newMsg)
                        | DumpDevicesState replyChannel -> 
                            replyChannel.Reply (sprintf "%A" msgDump)
                            return! locationAgentLoop msgDump
                      }
            locationAgentLoop MessageDump.Empty
        )
    member this.Exit() = printerMsgMailboxProcessor.Post(Exit)
    member this.Empty() = printerMsgMailboxProcessor.Post(Reset)
    member this.UpdateWith re = printerMsgMailboxProcessor.Post(UpdateWith re)
    member this.DumpDevicesState() = printerMsgMailboxProcessor.PostAndReply((fun reply -> DumpDevicesState reply), timeout = 2000)

let config = 
    let port = System.Environment.GetEnvironmentVariable("PORT")
    let ip127  = IPAddress.Parse("127.0.0.1")
    let ipZero = IPAddress.Parse("0.0.0.0")

    { defaultConfig with 
        bindings=[ (if port = null then HttpBinding.create HTTP ip127 (uint16 8080)
                    else HttpBinding.create HTTP ipZero (uint16 port)) ] }


let ws (logAgent:PrinterMsgAgent) (evt2Printer:IEvent<string>) (webSocket : WebSocket) (context: HttpContext) =

  let inbox = MailboxProcessor.Start (fun inbox -> async {
            let close = ref false
            while not !close do
                let! op, data, fi = inbox.Receive()
                do logAgent.UpdateWith (sprintf "Sending message to printer of type %A" op)
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

    do logAgent.UpdateWith "About to enter websocket read loop"

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
        do logAgent.UpdateWith response
        let jval = JsonValue.Parse str
        match jval.TryGetProperty "discovery_b64" with
        | Some str ->   do logAgent.UpdateWith "discovery_b64 property received. I am on main channel"
                        inbox.Post(Binary, UTF8.bytes """ { "configure_alert" : "ALL MESSAGES,SDK,Y,Y,,,N,|SGD SET,SDK,Y,Y,,,N,capture.channel1.data.raw" } """, true)
                        inbox.Post(Binary, UTF8.bytes """ { "open" : "v1.raw.zebra.com" } """, true)
        | None -> ()

        match jval.TryGetProperty "channel_name" with
        | Some str ->   let chanid = JsonExtensions.AsString (jval.GetProperty "channel_name")
                        do logAgent.UpdateWith (sprintf "Channel name: %s" chanid)
                        if chanid = "v1.raw.zebra.com" then
                           do evt2Printer |> Observable.subscribe (fun lbl -> do logAgent.UpdateWith (sprintf "Printing request")
                                                                              inbox.Post(Binary, UTF8.bytes lbl , true)) |> ignore
                        else
                           ()
        | None -> ()

      | (Ping, data, true) ->
        // Ping message received. Responding with Pong
        // The printer sends a PING message roughly ever 60 seconds. The server needs to respond with a PONG, per RFC6455
        // After three failed PING attempts, the printer disconnects and attempts to reconnect

        do logAgent.UpdateWith "Ping message from printer. Responding with Pong message"
        // A Pong frame sent in response to a Ping frame must have identical "Application data" as found in the message body of the Ping frame being replied to.
        // the `send` function sends a message back to the client
        do inbox.Post (Pong, data, true)

      | (Close, _, _) ->
        do logAgent.UpdateWith "Close message from printer!"
        do inbox.Post (Close, [||], true)

        // after sending a Close message, stop the loop
        loop <- false
        do pongTimer.Stop()

      | (_,_,fi) -> 
        do logAgent.UpdateWith (sprintf "Unexpected messagefrom printer of type %A" fi)
    
 }


/// An example of explictly fetching websocket errors and handling them in your codebase.
let wsWithErrorHandling (mAgent:PrinterMsgAgent) inbox (webSocket : WebSocket) (context: HttpContext) = 
   
   let exampleDisposableResource = { new IDisposable with member __.Dispose() = printfn "Resource needed by websocket connection disposed" }
   let websocketWorkflow = ws mAgent inbox webSocket context
   
   async {
    let! successOrError = websocketWorkflow
    match successOrError with
    // Success case
    | Choice1Of2() -> ()
    // Error case
    | Choice2Of2(error) ->
        // Example error handling logic here
        do mAgent.UpdateWith (sprintf "Error: [%A]" error)
        exampleDisposableResource.Dispose()
        
    return successOrError
   }

let app  : WebPart = 
  let mLogAgent = new PrinterMsgAgent()
  let evtPrint = new Event<string>()
  let toSendtoPrinter = evtPrint.Publish

  choose [
    path "/websocket" >=> handShake (ws mLogAgent toSendtoPrinter)
    path "/websocketWithSubprotocol" >=> handShakeWithSubprotocol (chooseSubprotocol "v1.weblink.zebra.com") (ws mLogAgent toSendtoPrinter)
    path "/websocketWithError" >=> handShake (wsWithErrorHandling mLogAgent toSendtoPrinter)
    GET >=> choose 
        [ path "/hello" >=> OK "Hello GET"
          path "/hellolabel" >=>  warbler (fun ctx -> evtPrint.Trigger(hellolabel); OK ("Triggered"))
          path "/logdump" >=> warbler (fun ctx -> OK ( mLogAgent.DumpDevicesState() ))
          path "/clearlog" >=> warbler (fun ctx -> OK ( mLogAgent.Empty(); "Log cleared" ))
          browseHome ]
    POST >=> choose
        [ path "/hello" >=> OK "Hello POST" ]
        // aggiungi POST "/printlabel" evtPrint.Trigger(body of POST)
    NOT_FOUND "Found no handlers." ]

//https://help.heroku.com/tickets/560930

[<EntryPoint>]
let main _ =
  startWebServer config app
  0

