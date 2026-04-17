// Prototype WebRTC data-channel transport for link-cable emulation.
// Signaling is manual paste-swap: one peer calls createOffer(), shares the
// returned SDP via out-of-band means (chat, etc.), the other calls
// acceptOffer() with that string and returns an answer, which the first peer
// feeds back through applyAnswer().
//
// The C# WebRtcLink drains the receive queue from ExchangeByte. No production
// use — no signaling server, no TURN, no reconnection handling.

window.kohWebRtcLink = (function () {
    let pc = null;
    let channel = null;
    let blazorRef = null;
    const receiveQueue = [];

    function onChannelOpen() {
        blazorRef?.invokeMethodAsync('OnLinkOpened');
    }

    function onChannelMessage(e) {
        const data = e.data instanceof ArrayBuffer ? new Uint8Array(e.data) : new Uint8Array(e.data.buffer ?? []);
        for (let i = 0; i < data.length; i++) receiveQueue.push(data[i]);
    }

    function wireChannel(ch) {
        channel = ch;
        channel.binaryType = 'arraybuffer';
        channel.onopen = onChannelOpen;
        channel.onmessage = onChannelMessage;
        channel.onclose = () => blazorRef?.invokeMethodAsync('OnLinkClosed');
    }

    return {
        register: function (dotNetRef) { blazorRef = dotNetRef; },

        createOffer: async function () {
            pc = new RTCPeerConnection();
            wireChannel(pc.createDataChannel('koh-serial', { ordered: true }));
            const offer = await pc.createOffer();
            await pc.setLocalDescription(offer);
            // Wait for ICE gathering so the SDP is complete before we hand it off.
            await new Promise(resolve => {
                if (pc.iceGatheringState === 'complete') { resolve(); return; }
                pc.addEventListener('icegatheringstatechange', () => {
                    if (pc.iceGatheringState === 'complete') resolve();
                });
            });
            return JSON.stringify(pc.localDescription);
        },

        acceptOffer: async function (offerJson) {
            pc = new RTCPeerConnection();
            pc.ondatachannel = (e) => wireChannel(e.channel);
            await pc.setRemoteDescription(JSON.parse(offerJson));
            const answer = await pc.createAnswer();
            await pc.setLocalDescription(answer);
            await new Promise(resolve => {
                if (pc.iceGatheringState === 'complete') { resolve(); return; }
                pc.addEventListener('icegatheringstatechange', () => {
                    if (pc.iceGatheringState === 'complete') resolve();
                });
            });
            return JSON.stringify(pc.localDescription);
        },

        applyAnswer: async function (answerJson) {
            if (!pc) throw new Error('call createOffer first');
            await pc.setRemoteDescription(JSON.parse(answerJson));
        },

        sendByte: function (byte) {
            if (channel?.readyState === 'open') {
                channel.send(new Uint8Array([byte]).buffer);
            }
        },

        drainReceived: function () {
            if (receiveQueue.length === 0) return -1;
            return receiveQueue.shift();
        },

        isOpen: function () { return channel?.readyState === 'open'; },

        close: function () {
            channel?.close();
            pc?.close();
            channel = null;
            pc = null;
            receiveQueue.length = 0;
        },
    };
})();
