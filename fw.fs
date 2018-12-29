module fw

open System
open System.Runtime.Serialization.Json
open System.Runtime.Serialization

open System.IO
open System.Xml
open System.Text

open JsonHelper


[<DataContract>]
type FwFile =
   { 
      [<field: DataMember(Name = "fwFileName")>]
      fwFileName : string;
   }

let fwFileList() = json<FwFile array> (Directory.GetFiles(".", "*.zpl") |>  (Array.map (fun s -> {fwFileName = Path.GetFileNameWithoutExtension s}) ))




