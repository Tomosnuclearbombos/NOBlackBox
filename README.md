# NOBlackBox

Mission Recorder for [Nuclear Option](https://store.steampowered.com/app/2168680/Nuclear_Option/)

## About this fork

This is my fork of [KopterBuzz/NOBlackBox](https://github.com/KopterBuzz/NOBlackBox), made with
KopterBuzz's OK. It's the build my two Nuclear Option servers run, and it's the optional recorder for
the live map in the [Nuclear Option Toolkit](https://github.com/Tomosnuclearbombos/nuclear-option-toolkit).
The recorder is KopterBuzz's and the contributors' work, listed at the bottom of this page, and these
are my changes on top of 0.3.8.5:

* **Pilot names.** Since game 0.34 the official build writes player callsigns as
  "A-19 (NuclearOption.Networking.PlayerName) 431". This build writes the pilot's name, and a Steam ID
  never goes in the callsign. With the toolkit's plugin installed it uses the same name the rest of
  the server shows.
* **Gun rounds.** On a dedicated server every gun round was being written without a position. Each
  round now has where it was fired from and where it ended up, and Tacview draws the line between
  them.
* **Performance.** The writer thread kept a whole CPU core busy whenever it had nothing to write, and
  every recorded object had three Unity calls a frame. The writer now sleeps until there's something
  to write, everything is ticked from one place, and gun rounds and flares no longer make a GameObject
  each.
* **Two memory leak fixes.** Finished gun rounds now leave the record of rounds already seen, and dead
  units, pilots and shockwaves are cleaned up properly, so those lists stop growing over a long
  mission.
* **Explosions.** Each explosion is recorded once, at its real blast radius. The original wrote one more
  size update after removing an explosion, which brought it back in Tacview, and my first leak fix had
  the recorder pick the same explosion up again every half second for about 10 seconds after the blast.
* **Main thread time (build 3).** This is the biggest change so far. The recorder was still searching the
  whole scene for new units every second and for explosions every half second, calling every recorded
  object twice a frame and turning every number into text on the game's main thread, which came to 54 to
  85 ms of every second on my PvE server with 16 or more players on. It now hears about new units,
  destroyed units and explosions from the game, only visits an object when its next update is due, and the
  writer thread builds the text, so it takes about 80% less of the main thread. A check build ran the old
  and new code side by side with 40 AI and they wrote the same 176,000 lines, however, dead missiles are
  no longer recorded two or three times, which my first leak fix had caused.
* **Smaller fixes (build 3).** EngineRPM is no longer written given its average started at NaN and it
  never became a number. A recording is also closed once now, as it could be closed two or three times at
  the end of a mission and make two zips of the same file.

The plugin GUID, version and config file are the same as the official build, so it drops in over it.
The download on the [releases page](https://github.com/Tomosnuclearbombos/NOBlackBox/releases) is the
whole `NOBlackBox` folder, so it unzips straight into `BepInEx/plugins/`, over an existing install too.
Of note, each player's Steam ID still goes in their aircraft's Registration label unless
`RecordSteamID = false` is set in `BepInEx/config/xyz.KopterBuzz.NOBlackBox.cfg`,
which is worth doing if you share your recordings.

The rest of this page is the original README.

---

This is a mod project that makes it possible to record your game sessions and review/analyse them using [Tacview](https://www.tacview.net/).

For Installation and Usage Guide, please see the [installation guide](./INSTALL.md).

For general discussion & chat, please join us on the [Nuclear Option Discord](https://discord.gg/nuclear-option-909034158205059082).

### Issues
To report an Issue with the mod, please use GitHub Issues on this repository.

### Contributing
If you wish to contribute, please fork this repository, implement your change in the fork and create a pull request.

### Authors & Contributors
- 7ep3s: Programming, Tacview customisation

- TunderTunder: Tacview customisation

- RehabRocket: Programming, Tacview customisation

- TYKUHN2: Programming

- CircuitButKerbin: Tacview customisation

- dv-dt-dino: Programming

- Klawifiantix: Tacview customisation

- Special thanks to Tacview developers for continuous support and assistance with this project!
- Special thanks to Shockfront Studios for making this amazing game and letting us mod it!
