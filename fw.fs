module fw

open System
open System.Runtime.Serialization

open System.IO
open JsonHelper
open Suave

open WebSocketUM
open MessageLogAgent
open PrintersAgent
open System.Diagnostics


[<DataContract>]
type FwFile =
   { 
      [<field: DataMember(Name = "fwFileName")>]
      fwFileName : string;
   }

let fwFileList() = json<FwFile array> 
                      (Directory.GetFiles(".", "*.zpl") |>  (Array.map (fun s -> {fwFileName = Path.GetFileNameWithoutExtension s}) ))

[<DataContract>]
type FwJobObj =
   { 
      [<field: DataMember(Name = "fwFile")>]
      fwFile : String;
      [<field: DataMember(Name = "id")>]
      id : String;
   }

let doFwUpgrade (fwJob:FwJobObj) (printersAgent: PrintersAgent) (mLogAgent:LogAgent) =
    // I don't use websocket continuation frames for firmware download
    async {
        let chunckSize = 4096  // tried with 2048 but seen no improvement
        let buffer = Array.zeroCreate chunckSize
        let finished = ref false
        let acc = ref 0L

        let stream = new FileStream ("./" + fwJob.fwFile + ".zpl", FileMode.Open)
        do mLogAgent.AppendToLog (sprintf "Starting fw upgrade %s > %s " fwJob.fwFile fwJob.id )

        while not finished.Value do
           // Download one (at most) 1kb chunk and copy it
           let! count = stream.AsyncRead(buffer, 0, chunckSize)
           do printersAgent.SendMsgOverRawChannel fwJob.id (Opcode.Binary, buffer, true) false
           acc := acc.Value + 1L
           if count < chunckSize then
              do mLogAgent.AppendToLog (sprintf "Frame #%u has size %d" acc.Value count)
           finished := count <= 0
           do! Async.Sleep 150 // seen problem if this sleep is removed

        do mLogAgent.AppendToLog (sprintf "FW Download queued-up (%u frames)  %s > %s" acc.Value fwJob.fwFile fwJob.id )
        do mLogAgent.AppendToLog (sprintf "Printer %s will not respond until fw upgrade process is complete  (it takes about 5 mins)" fwJob.id )

    } |> Async.Start



