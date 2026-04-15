# Phase 4 Real-Game Verification Checklists

These checklists are executed manually on a Koh emulator release build before
Phase 4 can close. Each game has a specific, repeatable sequence the verifier
performs in under 15 minutes.

Acquire ROMs legally (dump your own cartridges). Do not commit ROMs.

## Tetris (DMG)

- [ ] Title screen renders with no graphical glitches
- [ ] Title music plays at the correct tempo
- [ ] Selecting A-type game starts a game
- [ ] Pieces fall and lock in place
- [ ] Line clears update the score
- [ ] Sound effects on line clear play
- [ ] Game over screen reached
- [ ] Reset returns to the title screen

## Pokémon Blue (DMG)

- [ ] Intro cutscene plays without graphical glitches
- [ ] Title screen music plays
- [ ] "New Game" enters Oak's lab
- [ ] Player naming screen accepts input
- [ ] First battle versus rival completes through at least three turns
- [ ] Save to SRAM works (access Save menu, confirm)
- [ ] Reload from SRAM resumes at the saved location

## Pokémon Gold (CGB)

- [ ] Intro cutscene plays in CGB color
- [ ] Title screen renders
- [ ] New game reaches Professor Elm's lab
- [ ] CGB palette colors match reference screenshots in `fixtures/reference/pokemon-gold/`
- [ ] Save to SRAM with RTC works
- [ ] Reload after advancing the system clock shows RTC change in the intro screen

## Super Mario Land 2 (DMG)

- [ ] Title screen renders
- [ ] World map visible
- [ ] First level (Tree Zone) playable
- [ ] Mario moves, jumps, collects coins
- [ ] Enemies animate
- [ ] Pause menu opens
- [ ] Death returns to the world map

## Link's Awakening DX (CGB)

- [ ] CGB title screen renders in color
- [ ] Link's house loads
- [ ] Character movement works
- [ ] Tarin speaks via text box
- [ ] First screen transition loads adjacent room
- [ ] CGB palette transitions at dungeon entry work

## Verification log

Create a file `docs/verification/phase-4-YYYY-MM-DD.md` for each verification
run with pass/fail results and notes. Failures become test cases in
`Koh.Compat.Tests` wherever the failure can be triaged to a deterministic
reproducer.

Phase 4 cannot close until all five games pass their full checklists.
