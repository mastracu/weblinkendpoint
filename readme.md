# F# WeblinkEndpoint for Zebra Printers 

Demo app to demonstrate how to leverage Weblink technology to support a couple
of use-cases:

*  printing price labels (repricing) using ZQ320 indoor version (label printer with Wifi and BT) + CS4070 BT scanner
*  convertion of a 300 dpi label into a 200 dpi label – this is a real case from a recent customer request. For the customer modifying the printing app was just impossible.

Tested with ZQ320 indoor + CS4070, ZD420 203dpi Cartridge and ZT410 203 dpi

## Getting Started

Simple instructions follow


### Demo session setup

You will need to hook up your printer to the Internet (Configure Printer Connectivity in Zebra Setup Utility). I typically use Wifi printers and connect to ZGuest wireless network in Zebra office.
You then specify the address of the weblink endpoint by issuing 

```
! U1 setvar "weblink.ip.conn1.location" "https://weblinkendpoint.mastracu.it/websocketWithSubprotocol"
```
You then re-start / power-cycle the printer

Check the printer is now connected.
If it is, it will be listed in http://weblinkendpoint.mastracyu.it/appselector.html

The printers will also spit out a welcome label once it’s connected.

### Sending JSON configuration commands to the printer

Demonstrate how one can remotely send configuration commands to a printer by sending commands from appselector page and checking response on logtable.

### Repricing application

Minimum label width: 5cms 
Minimum label height: 3cms

On http://weblinkendpoint.mastracu.it/pricetag.html you add a new item that you have handy in the meeting room.

**Please ensure barcode is 13-digits long as only EAN-13 barcodes are currently supported.**

Once this is done I pair the CS4070 to the ZQ320 printer (you can use 123scan to print the related barcodes).

**You will also need to ensure the BT scanner is configured to CR-LF terminate the barcode read.**
 
I can then go ahead and scan the barcodes of the item above. 
A pricetag will be automatically printed if the barcode is found. 
I can then change the price of the item in the table on http://weblinkendpoint.mastracu.it/pricetag.html . I then scan the same product again and the label is printed with a different price. By selecting a product and a printer from http://weblinkendpoint.mastracu.it/pricetag.html I can also show how to print a label “remotely”.

**Please note - data are not persistent and application is restarted every 24 hours so price changes / new products will be lost the following day.**

### Label conversion

For the label conversion demo, from http://weblinkendpoint.mastracu.it/appselector.html
I change the application associated with any 200dpi printer to ifadlabelconvertion. 
Now everything I send to the printer via USB gets forwarded onto the cloud.

Send file IFAAM004_2289143IFAAM004.txt via USB and it gets converted to a quasi-equivalent 200 dpi label.

Minimum label width: 5cms 
Minimum label height: 3cms

## Versioning


## Authors


## License


## Acknowledgments

