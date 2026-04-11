export interface KohLaunchConfiguration {
    type: 'koh';
    request: 'launch';
    name: string;
    target?: string;
    program?: string;
    debugInfo?: string;
    hardwareMode?: 'auto' | 'dmg' | 'cgb';
    stopOnEntry?: boolean;
    preLaunchTask?: string;
}
