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

let doFwUpgrade (fwJob:FwJobObj) (agent: ChannelAgent) (mLogAgent:LogAgent) =
    // I don't use websocket continuation frames for firmware download
    async {
        let chunckSize = 4096  // tried with 2048 but seen no improvement
        let buffer = Array.zeroCreate chunckSize
        let prevbuffer = Array.zeroCreate chunckSize
        let finished = ref false
        let acc = ref 0L

        let stream = new FileStream ("./" + fwJob.fwFile + ".zpl", FileMode.Open)
        do mLogAgent.AppendToLog (sprintf "Starting fw upgrade %s > %s " fwJob.fwFile fwJob.id )

        while not finished.Value do
           let! count = stream.AsyncRead(buffer, 0, chunckSize)
           finished := count <= 0
           if (not finished.Value) then
              acc := acc.Value + 1L
              do agent.Post ((Opcode.Binary, buffer, true), false)              
              do! Async.Sleep 100 // seen problem if this sleep is removed
              if count < chunckSize then
                 do mLogAgent.AppendToLog (sprintf "Frame #%u has size %d" acc.Value count)
              else 
                 ()
              if buffer.[0..count-1] = prevbuffer.[0..count-1] then
                 do mLogAgent.AppendToLog (sprintf "Frame #%u is duplicated !!!" acc.Value)
              else
                 ()
              for i in 0 .. count-1 do
                 prevbuffer.[i] <- buffer.[i]                 
           else
              ()

        do mLogAgent.AppendToLog (sprintf "FW Download queued-up (%u frames)  %s > %s" acc.Value fwJob.fwFile fwJob.id )
        do mLogAgent.AppendToLog (sprintf "Printer %s will not respond until fw upgrade process is complete  (it takes about 5 mins)" fwJob.id )

    } |> Async.Start



