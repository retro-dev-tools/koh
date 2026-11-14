SECTION "Main", ROM0[$100]
    db HIGH(ext)
    db LOW(ext)
    db BANK(ext)
    db HIGH(ext + 1)
    ld a, LOW(ext)
    db LOW(HIGH(ext + $100))
    db BANK(ext) + 1
