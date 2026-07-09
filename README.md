# WonderGateway
WonderGateway is a reimplementation of WonderSwan's Mobile WonderGate Cable which connected your WonderSwan to a old Keitai. 
Building on top of Trap15 Wonderfence the goal of WonderGateway was to complete the WonderGate protocol and allow WonderSwan games to connect to a server and communicate like it was 2001 again. 

Having roughly 1000+ old Keitai at my disposal I was able to watch the WonderGate cable protocol in action and reverse engineer it, and make example servers for nearly all WonderSwan games today. Which you can find more information about on Keitai Archive.

# Features
- Emulator Support - WonderGateway is compatible with Mednafen
- Real Hardware Support - WonderGateway is compatible with all WonderSwan models, via a WonderWitch cable or a EXT-Friend.



# Settings

    Wondergateway.ini

> transport =

 "serial" for real hardware or "stdio" for emulator support

> comport =

 Set to the comport your WonderWitch/EXT-Friend is connected to

> baud =

 9600 or 38400 is supported

> reception =

 0-15 for signal strength, 0 is the zero signal, 15 is the full signal

> dialnumber =

 keep at 0000000000, cosmetic only

> ppp username =

 keep at "wonder", cosmetic only

> ppp password =

 keep at "gate", cosmetic only

> hostname_hack

Redirect domains to custome wonderswan servers, for example: *.mopera.ne.jp = 127.0.0.1

# How-To
Modify the WonderGateway.ini file to your liking, then run the WonderGateway.exe.


## Tested working games / With Example Servers Built
- MobileWonderGate
- Rainbow Islands
- Pocket Fighter
- RakuJongg
- Wizardry Scenario #1
- Terrors 2
- Buffers Evolution
- D's Garage 21 Koubo Game - Tane o Maku Tori
- Sennou Millennium
- Another Heaven - Memory of those Days
- Pocket no Naka no Doraemon
- Star Hearts
- Ring Infinity
- Digimon 02 - D1 Tamers
## In progress Test Servers
- Dark Eyes