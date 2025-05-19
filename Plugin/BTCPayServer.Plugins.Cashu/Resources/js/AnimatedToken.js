import {UR, UREncoder} from "@ngraveio/bc-ur";
import QRCode from "qrcode"

let tokenStr = document.getElementById("token-dummy-div").dataset.token;

document.addEventListener('DOMContentLoaded', generateAnimatedQRCode(tokenStr));


function generateAnimatedQRCode(data) {
    const qrCodeCanvas = document.getElementById('qrcode');

    let currentFragmentLength = 150;
    let currentFragmentInterval = 150;
    let encoder;
    let qrInterval;

    function startQrCodeLoop() {
        if (data.length === 0) {
            return;
        }

        const ur = UR.from(Buffer.from(data))
        const firstSeqNum = 0;
        encoder = new UREncoder(ur, currentFragmentLength, firstSeqNum);

        clearInterval(qrInterval);
        qrInterval = setInterval(() => {
            updateQrCode();
        }, currentFragmentInterval);
    }

    function updateQrCode() {
        const qrCodeFragment = encoder.nextPart();
        
        QRCode.toCanvas(qrCodeCanvas, qrCodeFragment, {
            width: 256,
            margin: 1,
            color: {
                dark: "#000000",
                light: "#ffffff"
            }
        }).catch(err => console.error(err));
    }
    

    startQrCodeLoop();
}