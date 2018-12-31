module PrintersAgent

open System
open System.Runtime.Serialization.Json
open System.Runtime.Serialization

open System.IO
open System.Xml
open System.Text
open FSharp.Data

open JsonHelper
open MessageLogAgent
open Suave.WebSocketUM

type ChannelFrame = Opcode*byte[]*bool
type ChannelAgent = MailboxProcessor<ChannelFrame*bool>

[<DataContract>]

type Printer =
   { 
      [<field: DataMember(Name = "uniqueID")>]
      uniqueID : string;
      [<field: DataMember(Name = "productName")>]
      productName : string;
      [<field: DataMember(Name = "appVersion")>]
      appVersion : string;
      [<field: DataMember(Name = "friendlyName")>]
      friendlyName : string;
      [<field: DataMember(Name = "sgdSetAlertFeedback")>]
      sgdSetAlertFeedback : string;
      // PriceTag | IfadLabelConversion
      [<field: DataMember(Name = "mainChannelAgent")>]
      mainChannelAgent : ChannelAgent;
      [<field: DataMember(Name = "rawChannelAgent")>]
      rawChannelAgent : ChannelAgent option;
      [<field: DataMember(Name = "configChannelAgent")>]
      configChannelAgent : ChannelAgent option;
      // only one entry per uniqueID - mainChannelFeed cannot be empty 
   }

let rec addPrinter id agent list =
      match list with
      | [] -> [{uniqueID = id;
               mainChannelAgent = agent; 
               productName = ""; 
               appVersion = ""; 
               friendlyName = ""; 
               sgdSetAlertFeedback = "ifadLabelConversion";
               rawChannelAgent = None;
               configChannelAgent = None}]
      | printHead :: xs -> if (printHead.uniqueID = id) 
                           then {printHead with mainChannelAgent = agent; 
                                                rawChannelAgent = None; 
                                                configChannelAgent = None;
                                                productName = ""; 
                                                appVersion = ""; 
                                                friendlyName = ""} :: xs 
                           else (printHead :: addPrinter id agent xs)

let rec removePrinter id channel list  =
      match list with
      | [] -> []
      | printer :: xs -> if (printer.uniqueID = id && printer.mainChannelAgent = channel) 
                           then xs 
                           else (printer :: removePrinter id channel xs)

let rec updatePartNumber id pn list =
      match list with
      | [] -> []
      | printer :: xs -> if printer.uniqueID = id 
                          then {printer with productName = pn} :: xs 
                          else (printer :: updatePartNumber id pn xs)

let rec updateAppVersion id ver list =
      match list with
      | [] -> []
      | printer :: xs -> if printer.uniqueID = id 
                          then {printer with appVersion = ver} :: xs 
                          else (printer :: updateAppVersion id ver xs)

let rec updateApp id appname list =
      match list with
      | [] -> []
      | printer :: xs -> if printer.uniqueID = id 
                          then {printer with sgdSetAlertFeedback = appname} :: xs 
                          else (printer :: updateApp id appname xs)


let rec updateRawChannel id agent list =
      match list with
      | [] -> []
      | printer :: xs -> if printer.uniqueID = id 
                          then {printer with rawChannelAgent = agent} :: xs 
                          else (printer :: updateRawChannel id agent xs)

let rec clearRawChannel id agent list =
      match list with
      | [] -> []
      | printer :: xs -> if (printer.uniqueID = id && printer.rawChannelAgent = agent)
                          then {printer with rawChannelAgent = None} :: xs 
                          else (printer :: clearRawChannel id agent xs)

let rec updateConfigChannel id agent list =
      match list with
      | [] -> []
      | printer :: xs -> if printer.uniqueID = id 
                          then {printer with configChannelAgent = agent} :: xs 
                          else (printer :: updateConfigChannel id agent xs)

let rec clearConfigChannel id agent list =
      match list with
      | [] -> []
      | printer :: xs -> if (printer.uniqueID = id && printer.configChannelAgent = agent)
                          then {printer with configChannelAgent = None} :: xs 
                          else (printer :: clearConfigChannel id agent xs)

let rec sendMsgOverMainChannel id frame ifLog list = 
      match list with
      | [] -> ()
      | printer :: xs -> if printer.uniqueID = id 
                         then printer.mainChannelAgent.Post (frame, ifLog)
                         else sendMsgOverMainChannel id frame ifLog xs

let rec sendMsgOverRawChannel id frame ifLog list = 
      match list with
      | [] -> ()
      | printer :: xs -> if printer.uniqueID = id 
                         then match printer.rawChannelAgent with
                              | None -> ()
                              | Some chan -> chan.Post (frame, ifLog)
                         else sendMsgOverRawChannel id frame ifLog xs

let rec sendMsgOverConfigChannel id frame ifLog list = 
      match list with
      | [] -> ()
      | printer :: xs -> if printer.uniqueID = id 
                         then match printer.configChannelAgent with
                              | None -> ()
                              | Some chan -> chan.Post (frame, ifLog)
                         else sendMsgOverConfigChannel id frame ifLog xs

let isKnownID id list = List.exists (fun printer -> printer.uniqueID = id) list
let tryFindPrinter id list = List.tryFind (fun (prt:Printer) -> prt.uniqueID = id) list

type PrintersAgentMsg = 
    | Exit
    | Clear
    | AddPrinter of string * ChannelAgent
    | UpdateRawChannel of string * (ChannelAgent option)
    | UpdateConfigChannel of string * (ChannelAgent option)
    | RemovePrinter of string * ChannelAgent 
    | ClearRawChannel of string * (ChannelAgent option)
    | ClearConfigChannel of string * (ChannelAgent option)
    | UpdatePartNumber of string * string
    | UpdateAppVersion of string * string
    | IsKnownID of string * AsyncReplyChannel<Boolean>
    | PrintersInventory  of AsyncReplyChannel<String>
    | FetchPrinterInfo of string * AsyncReplyChannel<Printer Option>
    | SendMsgOverMainChannel of string * ChannelFrame * bool
    | SendMsgOverRawChannel of string * ChannelFrame * bool
    | SendMsgOverConfigChannel of string * ChannelFrame * bool
    | UpdateApp of string * string

[<DataContract>]
type ConnectedPrinters = 
   { [<field: DataMember(Name = "connectedPrinters")>] PrinterList : Printer list } 
   static member Empty = {PrinterList = [] }


type PrintersAgent(logAgent:LogAgent) =
    let storeAgentMailboxProcessor =
        MailboxProcessor.Start(fun inbox ->
            let rec printersAgentLoop connPrts =
                async { 
                    let! msg = inbox.Receive()  
                    logAgent.AppendToLog (sprintf "Printersagent: message received %A" msg )
                    match msg with
                    | Exit -> return ()
                    | Clear -> return! printersAgentLoop ConnectedPrinters.Empty
                    | AddPrinter (id,chan) -> return! printersAgentLoop ({ PrinterList = addPrinter id chan connPrts.PrinterList})
                    | UpdateRawChannel (id,chan) -> return! printersAgentLoop ({ PrinterList = updateRawChannel id chan connPrts.PrinterList})
                    | UpdateConfigChannel (id,chan) -> return! printersAgentLoop ({ PrinterList = updateConfigChannel id chan connPrts.PrinterList})
                    | RemovePrinter (id,chan) -> return! printersAgentLoop ({ PrinterList = removePrinter id chan connPrts.PrinterList})
                    | ClearRawChannel (id,chan) -> return! printersAgentLoop ({ PrinterList = clearRawChannel id chan connPrts.PrinterList})
                    | ClearConfigChannel (id,chan) -> return! printersAgentLoop ({ PrinterList = clearConfigChannel id chan connPrts.PrinterList})
                    | UpdatePartNumber (id,pn) -> return! printersAgentLoop ({ PrinterList = updatePartNumber id pn connPrts.PrinterList})
                    | UpdateAppVersion (id,ver) -> return! printersAgentLoop ({ PrinterList = updateAppVersion id ver connPrts.PrinterList})
                    | UpdateApp (id,appname) -> return! printersAgentLoop ({ PrinterList = updateApp id appname connPrts.PrinterList})
                    | PrintersInventory replyChannel -> 
                        replyChannel.Reply (json<Printer array> (List.toArray connPrts.PrinterList))
                        return! printersAgentLoop connPrts
                    | IsKnownID (id, replyChannel) -> 
                        replyChannel.Reply (isKnownID id connPrts.PrinterList)
                        return! printersAgentLoop connPrts
                    | FetchPrinterInfo (id, replyChannel) -> 
                        replyChannel.Reply (tryFindPrinter id connPrts.PrinterList)
                        return! printersAgentLoop connPrts
                    | SendMsgOverMainChannel (id, frame, toLog) ->
                        match tryFindPrinter id connPrts.PrinterList with
                        | None -> ()
                        | Some prt -> prt.mainChannelAgent.Post (frame ,toLog)
                        return! printersAgentLoop connPrts
                    | SendMsgOverRawChannel (id, frame, toLog) ->
                        match tryFindPrinter id connPrts.PrinterList with
                        | None -> ()
                        | Some prt -> match prt.rawChannelAgent with
                                        | None -> ()
                                        | Some chanAgent -> chanAgent.Post (frame ,toLog)
                        return! printersAgentLoop connPrts
                    | SendMsgOverConfigChannel (id, frame, toLog) ->
                        match tryFindPrinter id connPrts.PrinterList with
                        | None -> ()
                        | Some prt -> match prt.configChannelAgent with
                                        | None -> ()
                                        | Some chanAgent -> chanAgent.Post (frame ,toLog)
                        return! printersAgentLoop connPrts
                }
            printersAgentLoop ConnectedPrinters.Empty
        )
    member this.Exit() = storeAgentMailboxProcessor.Post(Exit)
    member this.Empty() = storeAgentMailboxProcessor.Post(Clear)
    member this.AddPrinter id chan = storeAgentMailboxProcessor.Post(AddPrinter (id, chan))
    member this.UpdateRawChannel id chan = storeAgentMailboxProcessor.Post(UpdateRawChannel (id, chan))
    member this.UpdateConfigChannel id chan = storeAgentMailboxProcessor.Post(UpdateConfigChannel (id, chan))
    member this.RemovePrinter id chan = storeAgentMailboxProcessor.Post(RemovePrinter (id, chan))
    member this.ClearRawChannel id chan = storeAgentMailboxProcessor.Post(ClearRawChannel (id, chan))
    member this.ClearConfigChannel id chan = storeAgentMailboxProcessor.Post(ClearConfigChannel (id, chan))
    member this.UpdatePartNumber id pn = storeAgentMailboxProcessor.Post(UpdatePartNumber (id,pn))
    member this.UpdateAppVersion id ver = storeAgentMailboxProcessor.Post(UpdateAppVersion (id,ver))
    member this.PrintersInventory() = storeAgentMailboxProcessor.PostAndReply((fun reply -> PrintersInventory reply), timeout = 2000)
    member this.IsKnownID sku = storeAgentMailboxProcessor.PostAndReply((fun reply -> IsKnownID(sku,reply)), timeout = 2000)
    member this.FetchPrinterInfo id = storeAgentMailboxProcessor.PostAndReply((fun reply -> FetchPrinterInfo(id,reply)), timeout = 2000)
    member this.SendMsgOverMainChannel id frame toLog = storeAgentMailboxProcessor.Post(SendMsgOverMainChannel (id,frame,toLog))
    member this.SendMsgOverRawChannel id frame toLog = storeAgentMailboxProcessor.Post(SendMsgOverRawChannel (id,frame,toLog))
    member this.SendMsgOverConfigChannel id frame toLog = storeAgentMailboxProcessor.Post(SendMsgOverConfigChannel (id,frame,toLog))
    member this.UpdateApp id appname = storeAgentMailboxProcessor.Post(UpdateApp (id,appname))


