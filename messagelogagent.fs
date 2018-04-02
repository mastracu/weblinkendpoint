
module MessageLogAgent

open JsonHelper

type DumpAgentMsg = 
    | Exit
    | Reset
    | AppendToLog of string
    | LogDump  of AsyncReplyChannel<string>

type Log = 
   { msgList : string list } 
   static member Empty = {msgList = [] }
   member x.AppendToLog (msg:string) = 
      { msgList = (string System.DateTime.Now + " " + msg) :: x.msgList }

type PrinterMsgAgent() =
    let printerMsgMailboxProcessor =
        MailboxProcessor.Start(fun inbox ->
            let rec locationAgentLoop agentLog =
                async { let! msg = inbox.Receive()
                        match msg with
                        | Exit -> return ()
                        | Reset -> return! locationAgentLoop Log.Empty
                        | AppendToLog newMsg -> return! locationAgentLoop (agentLog.AppendToLog newMsg)
                        | LogDump replyChannel ->
                            replyChannel.Reply (json<string array> (List.toArray agentLog.msgList)) 
                            // replyChannel.Reply (sprintf "%A" msgDump)
                            return! locationAgentLoop agentLog
                      }
            locationAgentLoop Log.Empty
        )
    member this.Exit() = printerMsgMailboxProcessor.Post(Exit)
    member this.Empty() = printerMsgMailboxProcessor.Post(Reset)
    member this.AppendToLog newEntry = printerMsgMailboxProcessor.Post(AppendToLog newEntry)
    member this.LogDump() = printerMsgMailboxProcessor.PostAndReply((fun reply -> LogDump reply), timeout = 2000)
