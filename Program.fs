
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
open Suave.WebSocket

//TODO: https://github.com/SuaveIO/suave/issues/307

   
type DumpAgentMsg = 
    | Exit
    | Reset
    | UpdateWith of String
    | DumpDevicesState  of AsyncReplyChannel<String>

type MessageDump = 
   { LastMessage : String } 
   static member Empty = {LastMessage = "" }
   member x.UpdateWith (re:String) = 
      { LastMessage = re }

type PrinterMsgAgent() =
    let printerMsgMailboxProcessor =
        MailboxProcessor.Start(fun inbox ->
            let rec locationAgentLoop dsi =
                async { let! msg = inbox.Receive()
                        match msg with
                        | Exit -> return ()
                        | Reset -> return! locationAgentLoop MessageDump.Empty
                        | UpdateWith re -> return! locationAgentLoop (dsi.UpdateWith re)
                        | DumpDevicesState replyChannel -> 
                            replyChannel.Reply (sprintf "%A" dsi)
                            return! locationAgentLoop dsi
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


let ws (mAgent:PrinterMsgAgent) (webSocket : WebSocket) (context: HttpContext) =
  socket {

    // if `loop` is set to false, the server will stop receiving messages
    let mutable loop = true

    do mAgent.UpdateWith "in ws pre loop"

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
      | (Text, data, true) ->
        // the message can be converted to a string
        let str = UTF8.toString data
        let response = sprintf "Ugo su Heroku received: %s" str

        // the `send` function sends a message back to the client
        do mAgent.UpdateWith response

      | (Binary, data, true) ->
        // the message can be converted to a string
        let str = UTF8.toString data
        let response = sprintf "Ugo su Heroku received: %s" str

        // the `send` function sends a message back to the client
        do mAgent.UpdateWith response

      | (Close, _, _) ->
        let emptyResponse = [||] |> ByteSegment
        do! webSocket.send Close emptyResponse true

        // after sending a Close message, stop the loop
        loop <- false

      | _ -> ()
    }

/// An example of explictly fetching websocket errors and handling them in your codebase.
let wsWithErrorHandling (mAgent:PrinterMsgAgent) (webSocket : WebSocket) (context: HttpContext) = 
   
   let exampleDisposableResource = { new IDisposable with member __.Dispose() = printfn "Resource needed by websocket connection disposed" }
   let websocketWorkflow = ws mAgent webSocket context
   
   async {
    let! successOrError = websocketWorkflow
    match successOrError with
    // Success case
    | Choice1Of2() -> ()
    // Error case
    | Choice2Of2(error) ->
        // Example error handling logic here
        printfn "Error: [%A]" error
        exampleDisposableResource.Dispose()
        
    return successOrError
   }

let app (mAgent:PrinterMsgAgent) : WebPart = 
  choose [
    path "/websocket" >=> handShake (ws mAgent)
    // path "/websocketWithSubprotocol" >=> handShakeWithSubprotocol (chooseSubprotocol "test") ws
    path "/websocketWithError" >=> handShake (wsWithErrorHandling mAgent)
    GET >=> choose 
        [ path "/hello" >=> OK "Hello GET"
          path "/dump" >=> warbler (fun ctx -> OK ( mAgent.DumpDevicesState() ))
          browseHome ]
    NOT_FOUND "Found no handlers." ]

//https://help.heroku.com/tickets/560930

[<EntryPoint>]
let main _ =
  let msgAgent = new PrinterMsgAgent()
  startWebServer config (app msgAgent)
  0

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

