import * as path from 'path';
import * as fs from 'fs';
import { Logger } from '../core/Logger';

export interface KohBinaries {
    asm: string;
    link: string;
}

export function resolveKohBinaries(log: Logger): KohBinaries | null {
    const candidates = [
        path.join(__dirname, '..', '..', 'server'),
        path.join(__dirname, '..', '..', '..', '..', 'src', 'Koh.Asm', 'bin', 'Debug', 'net10.0'),
    ];

    for (const dir of candidates) {
        const asmExe = process.platform === 'win32' ? 'koh-asm.exe' : 'koh-asm';
        const linkExe = process.platform === 'win32' ? 'koh-link.exe' : 'koh-link';
        const asm = path.join(dir, asmExe);
        const link = path.join(dir, linkExe);
        if (fs.existsSync(asm) && fs.existsSync(link)) {
            log.info(`Found koh-asm/koh-link in ${dir}`);
            return { asm, link };
        }
    }

    log.warn('koh-asm / koh-link binaries not found in standard locations');
    return null;
}
