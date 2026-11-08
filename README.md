# Xbox Owned Library

A Playnite extension that adds the Xbox PC games you **own but have never launched**.

Playnite's built-in Xbox library builds its list from your Xbox title history, which only contains games you have started at least once. This extension reads your Microsoft Store collection instead, keeps the games that are playable on PC, and adds the missing ones.

## How it works

- Signs in with a device code (the app ID xbox.com uses, which is what the Store's collection service answers for).
- Reads every product on the account, keeps active owned games, and checks the public Store catalog for a PC package. Bundles count through the games inside them; demos, betas and add-ons are skipped.
- Adds each game **as an Xbox library game** (same game ID the built-in Xbox plugin uses), so the Xbox plugin installs and launches it, and later library updates don't create duplicates.
- Runs as part of Playnite's library update, so new games get metadata from your own metadata sources.

## Features

- Preview mode and a report of every owned game and what happened to it
- Optional tag for owned **Xbox Play Anywhere** games, new and already in the library
- Optional import of Games with Gold titles (tagged; playable only while subscribed)
- Optional Microsoft Store details and artwork instead of Playnite's metadata download
- Respects Playnite's import exclusion list

## Usage

1. Install the `.pext` from [Releases](https://github.com/sharkusmanch/playnite-xbox-owned-library/releases). The built-in Xbox library extension must be enabled.
2. **Add-ons → Extension settings → Libraries → Xbox Owned Library → Sign in**, and approve the sign-in in your browser.
3. Optionally run **Main menu → Extensions → Xbox Owned Library → Preview** to see what would be added.
4. Run **Update library** (or **Update library → Xbox Owned Library**).

## Not covered

- Game Pass titles you don't own
- Xbox 360 backward-compatible purchases (not in the Store collection)

## Building

Requires the .NET SDK and [Task](https://taskfile.dev).

```
task build    # build
task test     # unit tests
task pack     # build a .pext into dist/
task all      # clean, format, build, test, pack, install
```

## License

MIT
