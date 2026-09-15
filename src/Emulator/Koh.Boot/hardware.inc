; Hardware register EQUs for Koh's boot ROMs.
;
; A local copy on purpose: src/Emulator/Koh.Boot must not depend on anything under samples/.
; Only the registers the boot ROMs actually touch are here.

rP1     EQU $FF00   ; joypad
rDIV    EQU $FF04   ; divider
rNR11   EQU $FF11   ; ch1 length/duty
rNR12   EQU $FF12   ; ch1 volume envelope
rNR13   EQU $FF13   ; ch1 frequency low
rNR14   EQU $FF14   ; ch1 frequency high + trigger
rNR50   EQU $FF24   ; master volume
rNR51   EQU $FF25   ; sound panning
rNR52   EQU $FF26   ; sound on/off
rLCDC   EQU $FF40   ; LCD control
rSCY    EQU $FF42   ; background scroll Y
rLY     EQU $FF44   ; current scanline
rBGP    EQU $FF47   ; background palette
rOBP0   EQU $FF48   ; object palette 0
rOBP1   EQU $FF49   ; object palette 1
rVBK    EQU $FF4F   ; CGB VRAM bank
rBANK   EQU $FF50   ; boot ROM unmap latch
rBCPS   EQU $FF68   ; CGB background palette index
rBCPD   EQU $FF69   ; CGB background palette data
rOCPS   EQU $FF6A   ; CGB object palette index
rOCPD   EQU $FF6B   ; CGB object palette data
