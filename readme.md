# F# WeblinkEndpoint for Zebra Printers 

Weblink Workbench and printer "application" demo tool. "application" here has a very specific meaning: 
* a set of SGD configuration commands that redirect to cloud a specific input channel (USB, BT, SERIAL) - technically a list of JSON-SGD commands (typically a capture channel to variable and SGD-variable-change-alert setup) 
* some logic in the server that processes the alert and does something with it - technically an async computation expression that takes a string to a new string 

The 4 "applications" built into the server stem from 4 real-life use-cases where printer-redirected data is augmented / formatted appropriately before printing 

* printing price labels (repricing) using ZQ label printer with Wifi and BT + CS4070 BT scanner 
* convertion of a legacy label received via USB into a new label preserving variable data 
* self-service label printing in parcel shops for return of goods purchased online 
* convertion of SVG label (dpi independent) into ZPL (TO BE IMPLEMENTED)

## Consists of

* One wss / sse / https server written in F#/Suave
* One HTML5 SPA to monitor websocket channels and send commands onto the printer channels (HTML / JAVASCRIPT)

Requires F# tooling, npm, nuget to build.

Deployed to AWS EC2 - Docker running on a Linux AMI instance- . Amazon ALB terminates the HTTPS channel.

https://weblink.mastracu.it/console.html

## User Guide

See "Guide to Applications Demonstration" tab in https://weblink.mastracu.it/console.html

## Versioning

0.1 Dec 2018

## Authors

mastracu && ndz

## License

MIT 

## Acknowledgments

