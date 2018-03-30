module StoreAgent

open FSharp.Data

open System
open System.Runtime.Serialization.Json
open System.Runtime.Serialization

open System.IO
open System.Xml
open System.Text


/// Object to Json 
let internal json<'t> (myObj:'t) =   
        use ms = new MemoryStream() 
        (new DataContractJsonSerializer(typeof<'t>)).WriteObject(ms, myObj) 
        Encoding.Default.GetString(ms.ToArray()) 

/// Object from Json 
let internal unjson<'t> (jsonString:string)  : 't =  
        use ms = new MemoryStream(ASCIIEncoding.Default.GetBytes(jsonString)) 
        let obj = (new DataContractJsonSerializer(typeof<'t>)).ReadObject(ms) 
        obj :?> 't

[<DataContract>]
type Product =
   { 
      [<field: DataMember(Name = "sku")>]
      sku : string;
      [<field: DataMember(Name = "description")>]
      description : string;
      [<field: DataMember(Name = "unitPrice")>]
      unitPrice : int64;
      [<field: DataMember(Name = "eanCode")>]
      eanCode : string;
   }

let rec productUpdate prod list =
      match list with
      | [] -> []
      | prodHead :: xs -> if prodHead.sku = prod.sku then prod :: xs else (prodHead :: productUpdate prod xs)

type StoreAgentMsg = 
    | Exit
    | Clear
    | ProductUpdate of Product
    | IsKnownSKU of String * AsyncReplyChannel<Boolean>
    | StoreInventory  of AsyncReplyChannel<String>

[<DataContract>]
type Store = 
   { [<field: DataMember(Name = "productInStore")>] ProductList : Product list } 
   static member Empty = {ProductList = [] }
   member x.IsKnownSKU address = 
      List.exists (fun prod -> prod.sku = address) x.ProductList
   member x.ProductUpdate prod =  
      { ProductList = 
          if x.IsKnownSKU prod.sku then
              productUpdate prod x.ProductList
          else
              prod :: x.ProductList}

type StoreAgent() =
    let storeAgentMailboxProcessor =
        MailboxProcessor.Start(fun inbox ->
            let rec storeAgentLoop store =
                async { let! msg = inbox.Receive()
                        match msg with
                        | Exit -> return ()
                        | Clear -> return! storeAgentLoop Store.Empty
                        | ProductUpdate prod -> return! storeAgentLoop (store.ProductUpdate prod)
                        | IsKnownSKU (addr, replyChannel) -> 
                            replyChannel.Reply (store.IsKnownSKU addr)
                            return! storeAgentLoop store
                        | StoreInventory replyChannel -> 
                            replyChannel.Reply (json<Product array> (List.toArray store.ProductList))
                            return! storeAgentLoop store
                      }
            // storeAgentLoop Store.Empty
            // http://fsharp.github.io/FSharp.Data/library/Http.html
            let defaultjson = Http.RequestString("http://weblinkendpoint.mastracu.it/defaultinventory.json")
//            let newStore = { ProductList = [ {sku = "9342342"; description = "pasta"; unitPrice = 1213L; eanCode = "8901293874" };
//                                             {sku = "9342343"; description = "dough"; unitPrice = 712L; eanCode = "800223231" };
//                                             {sku = "9342344"; description = "beer"; unitPrice = 493L; eanCode = "72432423" }  ] }
            let newStore = { ProductList = Array.toList (unjson<Product array> defaultjson) } 
            storeAgentLoop newStore

        )
    member this.Exit() = storeAgentMailboxProcessor.Post(Exit)
    member this.Empty() = storeAgentMailboxProcessor.Post(Clear)
    member this.UpdateWith prod = storeAgentMailboxProcessor.Post(ProductUpdate prod)
    member this.IsKnownSKU addr = storeAgentMailboxProcessor.PostAndReply((fun reply -> IsKnownSKU(addr,reply)), timeout = 2000)
    member this.StoreInventory() = storeAgentMailboxProcessor.PostAndReply((fun reply -> StoreInventory reply), timeout = 2000)

