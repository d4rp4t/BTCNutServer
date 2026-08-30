using System.Text;
using BTCPayServer.Plugins.Cashu.Services;
using DotNut.BcUr;
using Xunit;

namespace BTCPayserver.Plugins.Cashu.Tests
{
    public class AnimatedQrEncoderTests
    {
        // a token short enough to fit into a single BC-UR part
        private const string ShortToken = "cashuBo2FteBtodHRwczovL21pbnQuZXhhbXBsZS5jb21hdWNzYXQ";

        private static string LongToken() => "cashuB" + new string('q', 1200);

        [Fact]
        public void EncodeParts_EmptyPayload_ReturnsNoParts()
        {
            Assert.Empty(AnimatedQrEncoder.EncodeParts(""));
            Assert.Empty(AnimatedQrEncoder.EncodeParts(null!));
        }

        [Fact]
        public void EncodeParts_ShortPayload_ReturnsSingleUnnumberedPart()
        {
            var parts = AnimatedQrEncoder.EncodeParts(ShortToken);

            var part = Assert.Single(parts);
            Assert.StartsWith("ur:bytes/", part);
            // a single part UR carries no seqNum-seqLen segment
            Assert.Equal(2, part.Split('/').Length);
        }

        [Fact]
        public void EncodeParts_LongPayload_ReturnsNumberedPartsWithRedundancy()
        {
            var parts = AnimatedQrEncoder.EncodeParts(LongToken());

            Assert.True(parts.Count > 2, "A long token should be split into several parts");
            Assert.All(parts, part => Assert.Matches(@"^ur:bytes/\d+-\d+/[a-z]+$", part));

            // the fragment count is announced by every part, and we emit twice as many
            var fragmentCount = int.Parse(parts[0].Split('/')[1].Split('-')[1]);
            Assert.Equal(fragmentCount * 2, parts.Count);
        }

        [Fact]
        public void EncodeParts_LongPayload_PartsReassembleIntoTheSameToken()
        {
            var token = LongToken();
            var decoder = new URDecoder();

            foreach (var part in AnimatedQrEncoder.EncodeParts(token))
            {
                if (decoder.IsComplete)
                {
                    break;
                }

                Assert.True(decoder.ReceivePart(part), $"Decoder rejected part: {part}");
            }

            Assert.True(decoder.IsSuccess);
            Assert.Equal(token, Encoding.UTF8.GetString(decoder.ResultUr!.ToBytes()));
        }

        [Fact]
        public void EncodeParts_DroppedFrames_StillReassembleThanksToTheFountainParts()
        {
            var token = LongToken();
            var parts = AnimatedQrEncoder.EncodeParts(token);
            var decoder = new URDecoder();

            // drop every third frame, as a camera that missed a beat would
            for (var i = 0; i < parts.Count && !decoder.IsComplete; i++)
            {
                if (i % 3 == 2)
                {
                    continue;
                }

                decoder.ReceivePart(parts[i]);
            }

            Assert.True(decoder.IsSuccess);
            Assert.Equal(token, Encoding.UTF8.GetString(decoder.ResultUr!.ToBytes()));
        }

        [Fact]
        public void Encode_ReturnsOnePngDataUriPerPart()
        {
            var token = LongToken();
            var frames = AnimatedQrEncoder.Encode(token);

            Assert.Equal(AnimatedQrEncoder.EncodeParts(token).Count, frames.Count);
            Assert.All(frames, frame =>
            {
                Assert.StartsWith("data:image/png;base64,", frame);
                var png = Convert.FromBase64String(frame["data:image/png;base64,".Length..]);
                Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png[..4]);
            });
        }
    }
}
