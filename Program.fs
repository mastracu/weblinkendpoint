
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

type RegionEventType = 
 | EnterRegion
 | ExitRegion
  
[<DataContract>]
type RegionEvent =
   { 
      [<field: DataMember(Name = "regionName")>]
      regionName : string;
      [<field: DataMember(Name = "deviceID")>]
      deviceID : string;
      [<field: DataMember(Name = "timeStamp")>]
      timeStamp : int64;
      [<field: DataMember(Name = "eventType")>]
      eventType : RegionEventType;
   }

let regionEventProcessor func:WebPart = 
   mapJson (fun (regEvent:RegionEvent) -> 
                        func regEvent
                        regEvent)

let rec updateDeviceState re list =
      match list with
      | [] -> []
      | reHead :: xs -> if reHead.deviceID = re.deviceID then re :: xs else (reHead :: updateDeviceState re xs)
 
type LocationAgentMsg = 
    | Exit
    | Reset
    | UpdateWith of RegionEvent
    | IsKnownDevice of String * AsyncReplyChannel<Boolean>
    | DumpDevicesState  of AsyncReplyChannel<String>

type DevicesState = 
   { LastRegionEventPerDevice : RegionEvent list } 
   static member Empty = {LastRegionEventPerDevice = [] }
   member x.IsKnownDevice address = 
      List.exists (fun devLoc -> devLoc.deviceID = address) x.LastRegionEventPerDevice
   member x.UpdateDeviceState re = updateDeviceState re x.LastRegionEventPerDevice
   member x.UpdateWith (re:RegionEvent) = 
      { LastRegionEventPerDevice = 
          if x.IsKnownDevice re.deviceID then
              x.UpdateDeviceState re
          else
              re :: x.LastRegionEventPerDevice}

type LocationAgent() =
    let locationAgentMailboxProcessor =
        MailboxProcessor.Start(fun inbox ->
            let rec locationAgentLoop dsi =
                async { let! msg = inbox.Receive()
                        match msg with
                        | Exit -> return ()
                        | Reset -> return! locationAgentLoop DevicesState.Empty
                        | UpdateWith re -> return! locationAgentLoop (dsi.UpdateWith re)
                        | IsKnownDevice (addr, replyChannel) -> 
                            replyChannel.Reply (dsi.IsKnownDevice addr)
                            return! locationAgentLoop dsi
                        | DumpDevicesState replyChannel -> 
                            replyChannel.Reply (sprintf "%A" dsi)
                            return! locationAgentLoop dsi
                      }
            locationAgentLoop DevicesState.Empty
        )
    member this.Exit() = locationAgentMailboxProcessor.Post(Exit)
    member this.Empty() = locationAgentMailboxProcessor.Post(Reset)
    member this.UpdateWith re = locationAgentMailboxProcessor.Post(UpdateWith re)
    member this.IsKnownDevice addr = locationAgentMailboxProcessor.PostAndReply((fun reply -> IsKnownDevice(addr,reply)), timeout = 2000)
    member this.DumpDevicesState() = locationAgentMailboxProcessor.PostAndReply((fun reply -> DumpDevicesState reply), timeout = 2000)

let config = 
    let port = System.Environment.GetEnvironmentVariable("PORT")
    let ip127  = IPAddress.Parse("127.0.0.1")
    let ipZero = IPAddress.Parse("0.0.0.0")

    { defaultConfig with 
        bindings=[ (if port = null then HttpBinding.create HTTP ip127 (uint16 8080)
                    else HttpBinding.create HTTP ipZero (uint16 port)) ] }


let ws (webSocket : WebSocket) (context: HttpContext) =
  socket {
    let msg1 = "message from Heroku server"
    // the response needs to be converted to a ByteSegment
    let byteResponse =
        msg1
        |> System.Text.Encoding.ASCII.GetBytes
        |> ByteSegment
    do! webSocket.send Text byteResponse true

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
      | (Text, data, true) ->
        // the message can be converted to a string
        let str = UTF8.toString data
        let response = sprintf "Ugo su Heroku: received %s" str

        // the response needs to be converted to a ByteSegment
        let byteResponse =
          response
          |> System.Text.Encoding.ASCII.GetBytes
          |> ByteSegment

        // the `send` function sends a message back to the client
        do! webSocket.send Text byteResponse true

      | (Close, _, _) ->
        let emptyResponse = [||] |> ByteSegment
        do! webSocket.send Close emptyResponse true

        // after sending a Close message, stop the loop
        loop <- false

      | _ -> ()
    }

/// An example of explictly fetching websocket errors and handling them in your codebase.
let wsWithErrorHandling (webSocket : WebSocket) (context: HttpContext) = 
   
   let exampleDisposableResource = { new IDisposable with member __.Dispose() = printfn "Resource needed by websocket connection disposed" }
   let websocketWorkflow = ws webSocket context
   
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

let app (locationAgent:LocationAgent) : WebPart = 
  choose [
    path "/websocket" >=> handShake ws
    // path "/websocketWithSubprotocol" >=> handShakeWithSubprotocol (chooseSubprotocol "test") ws
    path "/websocketWithError" >=> handShake wsWithErrorHandling
    GET >=> choose 
        [ path "/" >=> file "index.html"
          path "/hello" >=> OK "Hello GET"
          //https://stackoverflow.com/questions/40561394/f-suave-warbler-function
          path "/RegionStatusReport" >=> warbler (fun ctx -> OK ( locationAgent.DumpDevicesState() ))
          browseHome ]
    POST >=> choose
        [ path "/hello" >=> OK "Hello POST"
          path "/regionEvent" >=> regionEventProcessor locationAgent.UpdateWith  ]  
    NOT_FOUND "Found no handlers." ]

[<EntryPoint>]
let main _ =
  let locationAgent = new LocationAgent()
  startWebServer config (app locationAgent)
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

//E:\Documents\weblinkendpoint>heroku certs:add ZebraCAChain.cer weblinkendpoint.herokuapp.com.cer weblinkendpoint.herokuapp.com.key
//Resolving trust chain... done
//Adding SSL certificate to ? weblinkendpoint... done
//Certificate details:
//Common Name(s): weblinkendpoint.herokuapp.com
//Expires At:     2037-12-07 16:23 UTC
//Issuer:         /CN=zebradevice1
//Starts At:      2012-12-13 12:28 UTC
//Subject:        /C=IT/ST=ZEBRAIT/L=MILAN/O=ZEBRA/OU=ZIT25/CN=weblinkendpoint.herokuapp.com/emailAddress=ugo.mastracchio@zebra.com
//SSL certificate is not trusted.
//
//=== The following common names are for hosts that are managed by Heroku
//weblinkendpoint.herokuapp.com
//
//=== Your certificate has been added successfully.  Add a custom domain to your app by running ? heroku domains:add <yourdomain.com>