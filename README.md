# Visible Locker Interior Mod
This mod adds the display of items on the glass inside of the locker. 

## Screenshots

![plot](./Screenshots/scr1.jpg)
![plot](./Screenshots/scr2.jpg)

## Requirements

BepInEx 5 - latest

## Installation

Download released files and unpack it to BepInEx/plugins folder.

## Known issues

Swim charge fins and Ultra glide fins will appear very dark without
shining direct light (e.g. flashlight) upon them. The bug exist in
the original object so our best bet is to wait for the fix in vanilla.
Also, the Advanced Wiring kit and the Wiring kit are both using the Advanced Wiring Kit model.

## Support

I am NOT a modder, and have never modded Subnautica before, nor do I have any
expertise with the Unity engine, which Subnautica uses.  I have sufficient
expertise to dig into the original code to this mod (forked from
https://github.com/HenryJk/visible-interior-SN) and make the necessary changes
so that it works with the latest version of Subnautica. The changes made were
as minimalistic as possible to create a working mod.

I make no guarantees for continued support or updates to this mod.  I only did this
because it appeared that the original programmer may have abandoned the project
(it's been 3 years--and like all of us, we move on after we've had our fun). I was on
my first playthrough of Subnautic and found this mod very useful.

If a new update to Subnautica breaks this mod and I don't update it, it is probably
because I have moved on to other things.  For this reason, I provide the instructions
below for how you can make the fixes yourself.  Note that the BepInEx mod must already
be installed and working for these instructions to work.

1. Clone this repository.
2. Open the solution file in Visual Studio.
3. From the Subnautica_Data\Managed folder of your Subnautica installation, copy the CSharp.dll and
   CSharp-firstpass.dll files to the folder "Subnautica_Data\Managed" of this project.
4. From Visual Studio, open the Command Window and run:
   ```
   AssemblyPublicizer CSharp.dll
   AssemblyPublicizer CSharp-firstpass.dll
   ```
5. Build the project (this will automatically pull down the NuGet packages).
6. Fix any compiler errors that may occur and build the project.
7. Copy the resulting VisibleLockerInterior.dll file to your Subnautica BepInEx plugins folder.
8. copy Assembly-CSharp_publicized.dll and Assembly-CSharp-firstpass_publicized.dll to "Subnautica_Data\Managed\publicized_assemblies"
    of your Subnautica game folder.
9. Run Subnautica and hope it worked.

### Building against Subnautica 1.22 (changeset 83031)

Subnautica 1.22 removes the need for the publicizer step: every game member
this mod touches is public now. Copy `Assembly-CSharp.dll` and
`Assembly-CSharp-firstpass.dll` from your game install (in the 1.22 layout
they sit in the game root folder) to the repository root, then build the
project as usual. The two DLLs are gitignored.

## Contributing

This mod is compiled using Visual Studio Community Edition. You may
also need to publicize 2 Subnautica assemblies: CSharp.dll and
CSharp-firstpass.dll in Subnautica_Data/Managed folder so that you
can use private methods freely without using tedious methods like
reflection. Refer to
[this](https://github.com/CabbageCrow/AssemblyPublicizer) guide
to learn how to publicize assemblies

## License

[MIT](https://choosealicense.com/licenses/mit/)
