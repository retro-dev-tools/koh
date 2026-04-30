; Game Boy / Game Boy Color hardware register definitions.
; Hand-authored subset — only what the game actually touches.

; --- Joypad ---
rP1                EQU $FF00
P1F_GET_BTN        EQU %0010_0000
P1F_GET_DPAD       EQU %0001_0000
P1F_GET_NONE       EQU %0011_0000

; --- Interrupts ---
rIF                EQU $FF0F
rIE                EQU $FFFF
IEF_VBLANK         EQU %0000_0001
IEF_STAT           EQU %0000_0010
IEF_TIMER          EQU %0000_0100
IEF_SERIAL         EQU %0000_1000
IEF_HILO           EQU %0001_0000

; --- LCD ---
rLCDC              EQU $FF40
LCDCF_OFF          EQU %0000_0000
LCDCF_ON           EQU %1000_0000
LCDCF_WIN9C00      EQU %0100_0000
LCDCF_WIN9800      EQU %0000_0000
LCDCF_WINON        EQU %0010_0000
LCDCF_WINOFF       EQU %0000_0000
LCDCF_BG8000       EQU %0001_0000
LCDCF_BG8800       EQU %0000_0000
LCDCF_BG9C00       EQU %0000_1000
LCDCF_BG9800       EQU %0000_0000
LCDCF_OBJ16        EQU %0000_0100
LCDCF_OBJ8         EQU %0000_0000
LCDCF_OBJON        EQU %0000_0010
LCDCF_OBJOFF       EQU %0000_0000
LCDCF_BGON         EQU %0000_0001
LCDCF_BGOFF        EQU %0000_0000

rSTAT              EQU $FF41
STATF_LYC          EQU %0100_0000
STATF_MODE10       EQU %0010_0000
STATF_MODE01       EQU %0001_0000
STATF_MODE00       EQU %0000_1000
STATF_LYCF         EQU %0000_0100
STATF_HBL          EQU 0
STATF_VBL          EQU 1
STATF_OAM          EQU 2
STATF_LCD          EQU 3

rSCY               EQU $FF42
rSCX               EQU $FF43
rLY                EQU $FF44
rLYC               EQU $FF45
rDMA               EQU $FF46
rBGP               EQU $FF47   ; DMG only
rOBP0              EQU $FF48   ; DMG only
rOBP1              EQU $FF49   ; DMG only
rWY                EQU $FF4A
rWX                EQU $FF4B

; --- GBC-only ---
rKEY1              EQU $FF4D   ; speed switch
rVBK               EQU $FF4F   ; VRAM bank select
rHDMA1             EQU $FF51
rHDMA2             EQU $FF52
rHDMA3             EQU $FF53
rHDMA4             EQU $FF54
rHDMA5             EQU $FF55
rRP                EQU $FF56
rBCPS              EQU $FF68   ; BG palette index
rBCPD              EQU $FF69   ; BG palette data
rOCPS              EQU $FF6A   ; OBJ palette index
rOCPD              EQU $FF6B   ; OBJ palette data
rSVBK              EQU $FF70   ; WRAM bank select

BCPSF_AUTOINC      EQU %1000_0000
OCPSF_AUTOINC      EQU %1000_0000

; --- Sound ---
rNR10              EQU $FF10
rNR11              EQU $FF11
rNR12              EQU $FF12
rNR13              EQU $FF13
rNR14              EQU $FF14
rNR21              EQU $FF16
rNR22              EQU $FF17
rNR23              EQU $FF18
rNR24              EQU $FF19
rNR30              EQU $FF1A
rNR31              EQU $FF1B
rNR32              EQU $FF1C
rNR33              EQU $FF1D
rNR34              EQU $FF1E
rNR41              EQU $FF20
rNR42              EQU $FF21
rNR43              EQU $FF22
rNR44              EQU $FF23
rNR50              EQU $FF24
rNR51              EQU $FF25
rNR52              EQU $FF26
rWAVE_0            EQU $FF30   ; wave RAM, 16 bytes

; --- Timer ---
rDIV               EQU $FF04
rTIMA              EQU $FF05
rTMA               EQU $FF06
rTAC               EQU $FF07

; --- Memory regions ---
_VRAM              EQU $8000
_VRAM8000          EQU $8000
_VRAM8800          EQU $8800
_VRAM9000          EQU $9000
_SCRN0             EQU $9800
_SCRN1             EQU $9C00
_OAMRAM            EQU $FE00
_RAM               EQU $C000
_HRAM              EQU $FF80
_SRAM              EQU $A000

; --- MBC5 ---
rRAMG              EQU $0000   ; SRAM enable (write $0A)
rROMB0             EQU $2000   ; ROM bank low
rROMB1             EQU $3000   ; ROM bank high
rRAMB              EQU $4000   ; SRAM bank
