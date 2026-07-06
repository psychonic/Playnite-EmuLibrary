using EmuLibrary.RomTypes.Xbox360.Disc;
using System;
using System.Security.Cryptography;
using Xunit;

namespace EmuLibrary.Tests.RomTypes.Xbox360
{
    // Validates the LZX decoder against a real Microsoft-produced LZX stream. The vector was generated with
    // Windows makecab (CompressionType=LZX, CompressionMemory=16) over 40001 bytes of ASCII text containing no
    // 0xE8 byte, so the Intel-E8 call transform (which the XEX decoder deliberately omits, being for PowerPC) is
    // a no-op and CAB LZX output is byte-identical to what our E8-disabled decoder must produce. The raw LZX
    // stream (concatenated CAB CFDATA payloads) is embedded below; the SHA-256 pins the exact decoded output.
    //
    // This exercises the full engine: canonical Huffman table build + tree traversal, the MSB 16-bit-word bit
    // reader, verbatim/aligned block decoding, match copies, and the 32KB frame handling with per-frame realign
    // (the 40001-byte output spans two frames). Window wrap and larger windows were additionally verified
    // locally against 200KB window-15 and window-21 vectors.
    public class LzxTests
    {
        private const int WindowBits = 16;
        private const int OutputLength = 40001;
        private const string ExpectedSha256 = "c269160a4f98e3d77b47050086eea6679df50f87dadc942214c3fe173b5c9ca6";

        [Fact]
        public void DecodesMakecabLzxStream()
        {
            var stream = Convert.FromBase64String(LzxStreamBase64);

            var decoded = Lzx.Decompress(stream, WindowBits, OutputLength);

            Assert.NotNull(decoded);
            Assert.Equal(OutputLength, decoded.Length);
            Assert.Equal(ExpectedSha256, Sha256Hex(decoded));
        }

        [Fact]
        public void RejectsInvalidWindowBits()
        {
            var stream = Convert.FromBase64String(LzxStreamBase64);
            Assert.Null(Lzx.Decompress(stream, 14, OutputLength));
            Assert.Null(Lzx.Decompress(stream, 22, OutputLength));
        }

        [Fact]
        public void RejectsNullAndZeroLength()
        {
            Assert.Null(Lzx.Decompress(null, WindowBits, OutputLength));
            Assert.Null(Lzx.Decompress(new byte[8], WindowBits, 0));
        }

        private static string Sha256Hex(byte[] data)
        {
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(data);
                var sb = new System.Text.StringBuilder(hash.Length * 2);
                foreach (var b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        private const string LzxStreamBase64 =
            "W4CAjQkQFMQAACMyAAAAAFxTu76O/igJFMQFsDf48veAuBEADRFZDQyAMQIEewq9LJphBTOCzgDLrca8Ly0tTqlhcN8dIVTBuKvDNAKQABuw0Y8yIzhHcCPoEfQI+gS9oM/0l+gj0Efhj0IfCD2NcBCGAAAqGqICghoZmJlv3eZvbuJsQgU/BT8//D/fPMu3/PqXn/n1d7/y6V98+JMP//snvvk3L3/1/Cd8eauf+OXbf3n0sD/88vd/93X/w7M/+OXP+vxdb96/39yDbW7YC1Ic3vXuwd5O3z+DrnaRo0fydv+nw1PP7x/39NwOrde1qXBrXVXvsTkzuYf8PrUt7X5Bh8+h5CdXD/d28Kv9yQ/58U9+X/3yWMEn3/b7Ba47FHye9uiC4BYLvB/Yr1et3Htc2nFwk88+/3U9cE254P9vXDN/HpeshZqGDfuvaL0IsmPSu7q9N/uYMgp6W9U6yfzh8K3DD4LPxt9CLnf9rtjJ1eEOa/gvf+drCJf8//V2VP0F1T/9ob5Ke+f06KrAIXc8OZ3O/4Muq6xegBwrHXphkpP83QTc63unWZ/uv58nt9PHWR3G154S8mizHdIcHoRF37yHT+88/lp+BfcIhEOMPK6rK3b1WhxrvnjmfWoNOttcoGz9lVuuV01+TuQaLcKcjzTCQYmbRfyJgrz50QPh45oekfm4C9iPXQ23uXtj4x48fRHod1Qc0h3t6y0ZG7u8Lhzz5FTrLn84Viby4JP/Wu6O8JjkCqW3z7+M0vOjwDnqSqBXSbQu6adJfHcz50eV+VPzmOEgD8T4SuCB0piiUvJs1bfKikFvtalsDz1O3DDZiTYXveyMiC+/eTxEX8OVR+lzKc132HJU/qm2PeDWRg85h9xneohohDPptj77DT2fat5u6rmlIvzyfiGMiofabyAepjvmDmeqDsL6HbbxQp7mZYm2yPE1/IGvXH3B9aykOTc4E2GLRw6ogmBzpA3YuZtccbZkrWhVE6Z2xLu5wDHaD0YlXdgE6YbnIMdxJXdDDfmbbpGpRwYCsn5ZkrXc+7p938U7PE5m4Pujzs8hyGTUciR/EEk8/SugLrNKVzZxJH77J0BB/IjxjLvWuxn7N+x6bXCFEPyAqnnBLsne5M76kYuXnDPeBeuIiQ2R1MGAOPUSrU8lN+M+lbKErxg9MZvQu4fiO36RPavj4T/jzNXXaSWrS1O759KRL1Xlp6c5dEcnkJFa1I5Rj5VW5ziJL/Ef8sPXZwYesMcVyJyXM16ospkinSJQH/6FFOQt4rlAT+I+sTmD/8BTVpLR81SFF8FG75/gU0TLTNywJ3OxaIob4VE5/t9eKM1rFy2eXutHMLPVrQzr3FlqHjkGI8u7boRJA8DdjtFIn1LoE+5YKCaiWSNBN717x/ZX8IXeDJcSVGbV70H86JGEB8y48eerar3VREITckMSncq0bAPn5GjWRCnrAWfXjNKKfFQMbTnAdpaeWhCpt9kvudz/IVCMedkOlZxZPOkRT5lA1nHI6WoEJS/I+SALI3RFG/R//EKdhOUhIYVSh8kMqTmG1/iEryI7xg4OGnpjraMZWLBTwxGLn1pPPyZbPlPL2OrR5dXJMt4oAwN6JYuh6rYVECUv63+Blx2DDeRS3sEL6vQqVn94icvrP3a2QulRSkqVAWNp1el9fSOt+ycPKrUfPn3VI5IGhiiKPP65oK6coxXlsHXYcSGaE/+NyhZlEgXTPJ6es0a6xv/HsjJuMj21bYEJJbF0AaOfJIfM+uWNHVhk5WOy5LKvAG4qI5xIGlDA96784HlNWTvf3hrSa6jJ47UNCcVFIqJqfWpwRQJ6/LVI55RMUVTlqTBpqY17xKj5CgK0z8g5JvaXdwr6JIk66qg7BMXha67+KQ4nKpfoZRJyzRxaM4XJr99/R9yeiiAGIB3Ql/10JsubtanGMeRsvlmNwvbVQQ/w7yAVWSczxas3Hgg5XNbmLoOp3fDGp5TWNYFcAKWUnpkb3Aqk8ti+dJdNc/GGq1CfPdMbyp18GbVbI3NQb7VxUkvyq43UsP+yL4M6igD2TmIrV5e7or6K1muJnVIksehaIfg6tZ0f2T51rwLU8FTzK/+TppfJNmSo0DSPYn7vjgY+/lUofyhG3YUHVaKu0Cvn1NwBVeJ8gPsKQ1+jV7NGK1/oTsA+YQKFzUUsfYK+Tskvsy31oKwIsrYqUKx3Kx43DFn1s38tf3W1+r9C8tYGO1mxP5Ep/EyqhthAJd6XieFTdsTKM5cIQw34s1foF1ykBaNasGDoEpyWd+Xc4xrxsSFSwRYwdAAGRq8zZ8VbPDSSWBip8sGHYSJJFirNzIaWq4HV24IchAw7pThqEjBCR53UhuKgry/stHKslSA93ZIZP9AC/BPdpgq9ZE8ESTQRGlM3QmtzIwSDCJtMVt6XT6mvviSKtEIw9Fc35LfaQEtnsOQ1kbIYVC9J0DeQVORzn9yoqXJO29m1eBQ/RoFnbEgX/G0IQg5S0GJTKuAdlm4rjlIKUbzSbw+ttaD9D2KWoGDRnse3OyOXj6rhuPXbqlfBzJ1VBUNwniTVVSh5gfvGUlEUGFiUy8IohjG0eAR8ZxJvGGdCWxbx0sWUBMcm2Vmxq6dWNRCzXQlo5OfnBHkkAtwdE7ppoRKQsTMvYRX0eV+u/NPIGISZSq6l+3EvFaIvhmZJ10Ylhng4cUkts7lw/RR5h8MBlwLXUnEzn1hW12eW8pA3y/gp6B8zvU/Ltqm6DF8Czv51tFBOrYLMs43oS86pqasIqDbBRKQc0nRgp6xEM3OKLYufc/GsgWScq+zIR6DKPRde//YHSlx5kbOPJhCKew6lAcGAy4iE+gqOQKPBXh9AuJhHCsraNHZJW2hdShEVj1rMMbd0SKaCivBEmz7wFOvqETb3czCsaCHfq3WkToaea8PKtfRahCDmoFmlBakTyv5mB8Dwd6h53Snc+BLOvQOSEnnhXn8PakSSJm1dIGQ5tBrZqD/zdSkHx4xP1IWNGx79Wmotg/gVSCRTZROU7ob15f6erCebSDaDVagkzESupULp9qygxEU0AjXFwVWkJS+fo7TkHB86hoxojO1j4MDTyN6hgBCSKoshQChTZ0XprXLI23sHARYOU4l+4NSRHGulNYdWgUQRBf2g4lmflRnrNm6evAUxM0yjuOjfjDdA8RdzCgqIBJAoS1lEEiX+atWIljeaHYbf5cv9CLoat0zaa+TJHxpieBsaDRWV42eG7SOj8LSZ7heoUudrNrdm6HiBvIyaQyhuXIo2N4GrPDBHqUJQsBhF94+RySILLocgzXmbvsABW4+bIkWTLyPfMQk37prQeYpw1mOQ1B4tJ9ltqC8VUhnMdTIPhWOyyZvMhgcRmPTY+kBrTFq8kt0A0T6Clj/VZY94oLkK55LTdVRnPyRvIICUxtctpzAqVClaWJ0rTIbCnpTxdjChyi6Kwr4JltcSTniGiiVHnFRu5Jl0a0mzrjkz4DWvKbxyBCi3HCxwUxbeq0PqhvKBxP1xNP3c6n30QXpJep5zomTuWBlUM9aBcPC+SMoTh+Rj7UKWRWFa2HekaaGPdckK0HTG6YXWb1EappwEu9GdrUZ+imNPLEPiXWi7dR17sbWyMmz1G2XokZ4aWCklM/DnDqUhpQ3aOw3qFQwlao0Lb+qh2vRxS6zeT8v59MAk1G9LkZwD9skti4LId2YCDdYyduVEyvuBUQD/wEckbI0WLz8ZJW/Qh5oQUpRvZn1M7Q4XDlZSvbUdCjIGIz6CyvWVAX1UdrKYcQEuP9o4T3l2Ee+W9lazgM8RgUI4ajd6ipokT8mlQDipLGlRY4yvZIhRbSha+xjLwOVFEmurUXt3h+HPqCIjM6vDLBOGZKsHGGyAgk3Tbc58jofS3uZjQVlQk07n5nwCpLcybiCuBLNQY4sdqVMUDXnoSa+cwiuKtCl3nOGy2ehYI36I4iW3vCJpSKdcu83qzTfYGgbuuQy9eEZCxO60i8l4BADpcPQVtJU954jnSYOLWOrAuOWtWVGAXi3GPmJfL3qev82QaBPTEeMAXe8XlquI8CUvrTeKedJpy0gt0H7JV1Yg+UBENgdaYR6DTOCc5cqBqwNP79Z2rv5Y+GNFlEojUg+uu75NAG2xkKJcCufQjCyhFU5vOPEAZBGDhg8QyjVjPlEZNYpjjJ6G4UYO8yEVS+tcwc4aFinhI2/U2oW32vp9zpFgZHj8E3qlyydbrG+fyaEf1MlJPUaopH8zQkAdieGdBiajxV+707drYKokY8ju5RaVvmQOBzzlmrbUoEQFZWaN4LXhgZKJQtCQUKee7ngEMzoDZFohIXkXHMJNzJJ3ihyJ7CYG0AxaawuitGN4+qY0VpOqUjxPgN4+eCfCh4OHbHq2fibf8AoAXmJ+QBRG+qf+TptCIwM4GuY3VvbO9QZj9LREfFV4aKnvoHXY2qCvF1spIgmOnAYJx8LEA+GhoVPqshaUyg4FF0Yo8HbTil0Db1a7c4aZ9diLfzdCW35BiuI23cFB1djWL3Zqh2BxHcgw54XKLWe3CqWpVqxbN8iwGHL16vahF/4Jryrhtc6wavr4AQeCuTAkgOna26MTOBApmAeBEt946CNemew0I64YVs+SU0bDZxEqp1ZMyBzBjNjohCbA1iTWBDvGFaAi88fF7dE+prECXeDdwz8mT9BEIbCGIAaBkvZbj0Q09nlQrsxErS5d+s4rKYKmkZ1QjScyCW9nRDUEjphzxGplcT4GauLW5KdbT/EBczQUqBc8KFpvu+rJMuXWRXum8/qzaQbUrKQiiNA5jIh/XP1K46AXBZ2sa01ICBNA1D56alQ1QJzg4erm3QMZl2gVoKNTYOzO27dLwAeVHhFGgNIYK0m4rFYC/JpPwL5lq6BLX0bi4VGnVcvEHTli8ogCyCCAEsVkNPRJ6kgGoKExVTO5WQvNBUsGRe6/BmEDpzXgr8Y6Pa5q4xrYBRO+TVK3Cj+osZmFhaGfPK/jydLQotPbYhvHmIiPustAmW9aPAyPrFGEMshgJSCs16pKNPXBJCSMwin01M+WBcmkKE4GcyBoTSDgAhNXailSPk0mftGHVIO4x6K359Z7NkENNQaI/kgEMgYhkJhmqivov7u/v6wxHGjBq9NBhjOPS52ZA49MPqBu/jUzi5tR4KkB9oG6mpJpIlLSxG8pHbZx5TiYgofZUBrt/DpSUn9mpZoGKvkIW/9ttHjexHpcvePcXJkuaBJqqM4c9Vcg2ZES9/IhDX97viUPKi/DgRuYRKK+9+MQp8RGpVErGZkAUF7ZZ5czDMci043X1a7+5kEn6ZMz4QNSIZro+2wLee/1E420YgjQTwi9DUebYdxIsdMdzyi0bBL+xIcqoep+WRyI8Fipm4JT/6TZOJgSEOUu5bOmseEOgPEJRZKQj3RK7lnNIZ7VTw7G7WlAbmd5QbsnCZSGTz+j9YR0JJrJLqfqHLTLQaejpnWI1MLjGjzNfcQynehavZT5NjaDAMZEG7VM8Yo39eI3Oza8rAsWgWZSMhOImZcAwuT0ioa1elk2sY/mu8FtFEMk8mBFAgUEUF2cIadQGY/9w/PnHGTCRz0NBHp3/4RH24auTMyytu4xt4shQvKs/grz3S1ikl3KtVXV+c5cDrOt/UM/e11po7l22o0Be320G540TxH/R/q4ONgv9smgZyzZELEShjPe9dSjkq4L6OQywInEkQpYMTqIr8cRfLyKmoYQeFizPN0hZ8yubrTRx06BSHtSjM7f1rBsl+gl/88Hqsv5hMzjlasltauCyjJqpqFDATQVop69zhxLESlG0SSm+pslMLQn9HgbcrfuI6JKpJsGhv2r2EsaZIINoig3TzXQ2r9QAulSObGzTuDi/5wAOMjjM3bFnSE0imetVWBmo7PuSBBu2OUNsbIZVeBuZp5MmygIEg+qR7Rvsi0xrvlMA8AOENesIq/2gDaFuOO0xaqcAytaH4bgT/epgiWoBlMlWkkNpepzU1vjwdSLwr44zqcv80BaVlyjMepPHTtCpm/zYbGe5iz7wMt1v+z5pAeJTBy+QU/Qh6fWToGH07jtKM8ghIyWBPee9i6xN1qRlhTrmloJ/1BDumkfgZ6K2dPwpoontG0nEIbjkSxjc5A604FvzZe26T98TB3oEo2IA5iolmiH/8a/N3Exw9xnnUPnH/is8p5Pg5LRfjWRR0nGvydUkSCl0jQrmtO1kzTYwvu89kCZkAQQXqP5ggE6jmkkuM/Sm2AOOBq03suhJxAqbFTDzNRZjFg50+kKN2z8QY4R3dUyU2m955Q0F1MFsg1acDyyyCFYNvTEnWH5mBUuNsqNXKQPpKM1hR03esqS3VXrpgGto6/BnUuGCIwRizSOgOC00LMWBBvE7uV+tkIoiRsKcgdoDxTZsvQt+mhNPcEUXKtsRJfANG2ilo3a6+AByGMwP3I2nEpvU0D362ZUeEB61Ido5Kx7lYddlj6l1/zNZHTf03eXYoMrHu0NAip1/LxFsqeV6wskvnhgl8SzDbFGaA0/4AZ8hZJqF2qGAtL8t6nB1CYaTrGDRflCwsZIZjwrkptv+b42UktTn2VMoWqePXy1VhovtVlhjT+2OIapsfiiOCoV2wPmO3utOQRBZUaif8eauIv5WLx2Euwv70DigI1W8EbDbR5pnGNXG6Ix4TTPE7fC9A7TQmjK7cEvZSJ8i5mWzglvTj8RWTXs9eA6ALYkUJJ4eEBDQP3N+WQ+jPj35DL94DIhVryEAGTebLGePWro/WBfXisowOgeCBjXYAMGQHeU4CMdF2Eubc87ZxNFPmQL7gOlm0jNKnFFA8GftCB+JnrF1LBgbdq43s3hDFEnRE56KTUFWHRfLSdzkfxLkG8a5OSBBdlA+bXqm2HiINtK3pJ+pN6YWYHbHGlyn2N7julZ5RftJLLlBQ8ovRLZZhaLWmQnfc/wSzUVitlasW+gs5KIz0FkNVqneGlJBOGmUikeYeSLI2EADt5O7Dxt2bHgnXVG/XcMBZwEf5yc96XoTEgKbm9XWFXJwe9uOwD0UDaNzSuGIKRb1npd7hUkUGUhaMZFwiDgOpq0Qh2daZO24qKYKRNtFb7EdBqph7NVixc1F5cBB0YS9lM9MTOPp/BwNHsYjKeIKJRdESs2n8vB+e2RNwmS23PP4GSSE55G+Ro/mrscTJPEUa2UC8yBtzw6ARMBqV81DLbhynog9UypE9ITv0V7UE4+Pbm0M4vzfUNdIa0ePQctozWdiaM+7sr0JNWjdnvtxorths3MgT20XIw40MpftQAZRaKHFGErDWsLpQI8m1+l751sKmZDdkmdWUbN4SCf4Wy+6dTUGSDkykjlajjhlHRGhHVgsxFZ4jlBBmsUwmCyIGGaYXDxaltLEsImnYeZXlqqz0lK6Odj2k3MkGwMRNjFEUJF2H+ZAAkdKfhEH+c2ZZpdcwOfTVPACWntzTh583wCPQjIQs2SnpvSmbH8A/afZ91B8RDUZHvFmMDJTAfJKhU+3l47F1Vqr5Kpid3j2AB6gW1NDZ34vexvz6rTS9bs5KuPnVBfgIYsr6AefOdwEiZpfYHnWXuxzzQcNamHoSvaE1C7I3E6hUwXg2T/vj2M0Z5tJsx1rJO1tcUAryIPEcAzpCD6XuY4kE19bewRTZY4Xz6xSAYNfNzuM80/8nRbmPXLdKuQMq8dkPZKKDXNNpLhqrpa/NpEOuYmePtEZkVNu3YQElk3oESh3AjDpdwA2Gq3ndgVxQUoP/caNQk9u/m5SoxSR449B0zzb2CXqOlPi9WEvDHQc49w6N5MvCBAdiPY69vFWVNkqk8VoCBRPeS+3Uo8acsMS5iB/Ni+Fc+MKzW5WxYjTTP3DAOnEtI0jeS8C4ytZDzTGQtL+Nk+bKyoOtXUWYO3f9Y8jBL+/VjVYEwpo9p1AAP1MM5aowW+mRX7BUWAqTtt1lsiyxMLom0lb3Y8BrIalM3gNkTEiGnTxf4MdLPkNPqZuqQYWUsLUGuabkScsWw+datpGsE+smn6zWStAyb2My/F9sJDbfZxAF3RpJj0cAts7yTpQCSVAAY=";
    }
}
