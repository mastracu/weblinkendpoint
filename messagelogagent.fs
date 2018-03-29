
module MessageLogAgent

type DumpAgentMsg = 
    | Exit
    | Reset
    | UpdateWith of string
    | DumpDevicesState  of AsyncReplyChannel<string>

type MessageDump = 
   { DebugLog : string list } 
   static member Empty = {DebugLog = [] }
   member x.UpdateWith (msg:string) = 
      { DebugLog = (string System.DateTime.Now + " " + msg) :: x.DebugLog }

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
