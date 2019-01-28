
let parolaIT = "città"B;;
let defaultTable = System.Text.Encoding.Default.GetString parolaIT;;
let table850 = System.Text.Encoding.GetEncoding(850).GetString parolaIT;;
let table1252 = System.Text.Encoding.GetEncoding(1252).GetString parolaIT;;
let table1250 = System.Text.Encoding.GetEncoding(1250).GetString parolaIT;;
let utf8 = System.Text.Encoding.UTF8.GetString parolaIT;
printfn "%d" System.Text.Encoding.Default.CodePage
