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
   }

let rec printerUpdate prod list =
      match list with
      | [] -> []
      | prodHead :: xs -> if prodHead.uniqueID = prod.uniqueID then prod :: xs else (prodHead :: printerUpdate prod xs)


type PrintersAgentMsg = 
    | Exit
    | Clear
    | PrinterUpdate of Printer
    | IsKnownID of string * AsyncReplyChannel<Boolean>
    | PrintersInventory  of AsyncReplyChannel<String>

[<DataContract>]
type Store = 
   { [<field: DataMember(Name = "connectedPrinters")>] PrinterList : Printer list } 
   static member Empty = {PrinterList = [] }
   member x.IsKnownID id = 
      List.exists (fun printer -> printer.uniqueID = id) x.PrinterList
   member x.PrinterUpdate prt =  
      { PrinterList = 
          if x.IsKnownID prt.uniqueID then
              printerUpdate prt x.PrinterList
          else
              prt :: x.PrinterList}

type PrintersAgent() =
    let storeAgentMailboxProcessor =
        MailboxProcessor.Start(fun inbox ->
            let rec storeAgentLoop store =
                async { let! msg = inbox.Receive()
                        match msg with
                        | Exit -> return ()
                        | Clear -> return! storeAgentLoop Store.Empty
                        | PrinterUpdate prod -> return! storeAgentLoop (store.PrinterUpdate prod)
                        | IsKnownID (sku, replyChannel) -> 
                            replyChannel.Reply (store.IsKnownID sku)
                            return! storeAgentLoop store
                        | PrintersInventory replyChannel -> 
                            replyChannel.Reply (json<Printer array> (List.toArray store.PrinterList))
                            return! storeAgentLoop store
                      }
            // http://fsharp.github.io/FSharp.Data/library/Http.html
            let defaultjson = Http.RequestString("http://weblinkendpoint.mastracu.it/defaultinventory.json")
            let newStore = { PrinterList = Array.toList (unjson<Printer array> defaultjson) } 
            storeAgentLoop newStore

        )
    member this.Exit() = storeAgentMailboxProcessor.Post(Exit)
    member this.Empty() = storeAgentMailboxProcessor.Post(Clear)
    member this.UpdateWith prod = storeAgentMailboxProcessor.Post(PrinterUpdate prod)
    member this.IsKnownID sku = storeAgentMailboxProcessor.PostAndReply((fun reply -> IsKnownID(sku,reply)), timeout = 2000)
    member this.StoreInventory() = storeAgentMailboxProcessor.PostAndReply((fun reply -> PrintersInventory reply), timeout = 2000)

