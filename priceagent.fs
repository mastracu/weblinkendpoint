
module PriceAgent

type PriceAgentMsg = 
    | Exit
    | UpdatePrice of string
    | GetPrice  of AsyncReplyChannel<string>

type PriceAgent() =
    let priceMailboxProcessor =
        MailboxProcessor.Start(fun inbox ->
            let rec priceAgentLoop itemprice =
                async { let! msg = inbox.Receive()
                        match msg with
                        | Exit -> return ()
                        | UpdatePrice itemprice -> return! priceAgentLoop (itemprice)
                        | GetPrice replyChannel -> 
                            replyChannel.Reply itemprice
                            return! priceAgentLoop itemprice
                      }
            priceAgentLoop "1"
        )
    member this.UpdatePrice re = match re with 
                                 | None -> ()
                                 | Some s -> priceMailboxProcessor.Post(UpdatePrice s)

    member this.Exit() = priceMailboxProcessor.Post(Exit)
    member this.GetPrice() = priceMailboxProcessor.PostAndReply((fun reply -> GetPrice reply), timeout = 2000)

