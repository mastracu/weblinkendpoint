module PrintersAgent

open System
open System.Runtime.Serialization.Json
open System.Runtime.Serialization

open System.IO
open System.Xml
open System.Text

open FSharp.Data

open JsonHelper

[<DataContract>]
type Printer =
   { 
      [<field: DataMember(Name = "uniqueID")>]
      uniqueID : string;
      [<field: DataMember(Name = "partNumber")>]
      partNumber : string;
      [<field: DataMember(Name = "appVersion")>]
      appVersion : string;
      [<field: DataMember(Name = "friendlyName")>]
      friendlyName : string;
   }

let rec printerUpdate prod list =
      match list with
      | [] -> []
      | prodHead :: xs -> if prodHead.uniqueID = prod.uniqueID then prod :: xs else (prodHead :: printerUpdate prod xs)

let rec removePrinter id list =
      match list with
      | [] -> []
      | prodHead :: xs -> if prodHead.uniqueID = id then xs else (prodHead :: removePrinter id xs)


type PrintersAgentMsg = 
    | Exit
    | Clear
    | PrinterUpdate of Printer
    | IsKnownID of string * AsyncReplyChannel<Boolean>
    | RemovePrinter of string 
    | PrintersInventory  of AsyncReplyChannel<String>

[<DataContract>]
type ConnectedPrinters = 
   { [<field: DataMember(Name = "connectedPrinters")>] PrinterList : Printer list } 
   static member Empty = {PrinterList = [] }
   member x.IsKnownID id = 
      List.exists (fun printer -> printer.uniqueID = id) x.PrinterList
   member x.RemovePrinter id = 
      { PrinterList = (removePrinter id x.PrinterList) }
   member x.PrinterUpdate prt =  
      { PrinterList = 
          if x.IsKnownID prt.uniqueID then
              printerUpdate prt x.PrinterList
          else
              prt :: x.PrinterList}

type PrintersAgent() =
    let storeAgentMailboxProcessor =
        MailboxProcessor.Start(fun inbox ->
            let rec printersAgentLoop connPrts =
                async { let! msg = inbox.Receive()
                        match msg with
                        | Exit -> return ()
                        | Clear -> return! printersAgentLoop ConnectedPrinters.Empty
                        | PrinterUpdate prod -> return! printersAgentLoop (connPrts.PrinterUpdate prod)
                        | RemovePrinter id -> return! printersAgentLoop (connPrts.RemovePrinter id)
                        | IsKnownID (id, replyChannel) -> 
                            replyChannel.Reply (connPrts.IsKnownID id)
                            return! printersAgentLoop connPrts
                        | PrintersInventory replyChannel -> 
                            replyChannel.Reply (json<Printer array> (List.toArray connPrts.PrinterList))
                            return! printersAgentLoop connPrts
                      }
            printersAgentLoop ConnectedPrinters.Empty
        )
    member this.Exit() = storeAgentMailboxProcessor.Post(Exit)
    member this.Empty() = storeAgentMailboxProcessor.Post(Clear)
    member this.UpdateWith prod = storeAgentMailboxProcessor.Post(PrinterUpdate prod)
    member this.RemovePrinter id = storeAgentMailboxProcessor.Post(RemovePrinter id)
    member this.IsKnownID sku = storeAgentMailboxProcessor.PostAndReply((fun reply -> IsKnownID(sku,reply)), timeout = 2000)
    member this.PrintersInventory() = storeAgentMailboxProcessor.PostAndReply((fun reply -> PrintersInventory reply), timeout = 2000)

