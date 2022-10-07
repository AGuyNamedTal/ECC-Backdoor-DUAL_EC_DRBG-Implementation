﻿using System;
using System.Drawing;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Console = Colorful.Console;

// ReSharper disable InconsistentNaming

namespace TalV.ECCBackdoor
{
    public class ECRngStateCracker
    {


        public readonly ECRngParams RngParams;
        /// <summary>
        /// The secret value for each the point P on the curve is P=Q*e
        /// </summary>
        public readonly BigInteger e;
        private readonly EllipticCurve _curve;


        /// <summary>
        /// 
        /// </summary>
        /// <param name="rngParams">The parameters for the generator trying to crack</param>
        /// <param name="e">The secret value for each the point P on the curve is P=Q*e</param>
        public ECRngStateCracker(ECRngParams rngParams, BigInteger e)
        {
            RngParams = rngParams;
            this.e = e;
            _curve = rngParams.Curve;
        }

        public ECRng Crack(byte[] random)
        {
            int rngOutputSize = _curve.FieldSize / 8 - ECRng.TrimmedBytes;
            int rngOutputRounds = random.Length / rngOutputSize;
            if (rngOutputRounds < 2)
            {
                throw new ArgumentException($"Not enough data, need at least {rngOutputSize * 2}bytes");
            }
            Console.WriteLine("Splitting data into two rounds: ", AppColors.Attacker);
            byte[] round1 = random.Take(rngOutputSize).ToArray();
            byte[] round2 = random.Skip(rngOutputSize).Take(rngOutputSize).ToArray();
            Console.WriteLine($"Round 1: {round1.ToHex()}", AppColors.Attacker);
            Console.WriteLine($"Round 2: {round2.ToHex()}", AppColors.Attacker);


            ECRng foundRng = null;
            int completed = 0;
            // if using more than 3 bytes (ECRng.TrimmedBytes > 3), change type of possibleOptionsCount to something appropriate (long, ulong, bigint)
            const int possibleOptionsCount = 1 << (ECRng.TrimmedBytes * 8); // 2^16

            string FormatProgress(int done)
            {
                return ((double)done / possibleOptionsCount * 100).ToString(
                     "#0.00") + "%";
            }

            Console.WriteLine($"Total options to go through missing {ECRng.TrimmedBytes * 8}" +
               $" bits: {possibleOptionsCount}", AppColors.Attacker);

            CancellationTokenSource cts = new CancellationTokenSource();
            try
            {
                Console.WriteLine($"Starting cracking operation on {Environment.ProcessorCount} threads", AppColors.Attacker);
                Console.Write("Searching all possibility space: ", AppColors.Attacker);
                Point progressCursorPos = new Point(Console.CursorLeft, Console.CursorTop);
                Console.WriteLine(FormatProgress(0), AppColors.Attacker);
                Parallel.For((long)0, possibleOptionsCount + 1, new ParallelOptions
                {
                    CancellationToken = cts.Token,
                    MaxDegreeOfParallelism = Environment.ProcessorCount
                }, i =>
                {

                    ECRng rng = GuessRQ((int)i, round1, round2);
                    if (rng != null)
                    {
                        foundRng = rng;
                        cts.Cancel();
                    }
                    else
                    {
                        int completedLocal = Interlocked.Increment(ref completed);
                        if (completedLocal % 50 == 0)
                        {
                            Console.SetCursorPosition(progressCursorPos.X, progressCursorPos.Y);
                            Console.WriteLine(FormatProgress(completedLocal), AppColors.Attacker);
                        }
                    }

                });

            }
            catch (OperationCanceledException)
            {
                // ignore
            }
            finally
            {
                cts.Dispose();
            }

            if (foundRng == null)
            {
                return null;
            }
            Console.WriteLine($"Found state of RNG", AppColors.Attacker);
            int randomDataToSkip = random.Length - rngOutputSize * 2;
            Console.WriteLine($"Skipping {randomDataToSkip} bytes", AppColors.Attacker);
            byte[] buffer = new byte[randomDataToSkip];
            foundRng.Next(buffer);
            return foundRng;
        }

        private ECRng GuessRQ(int currentGuess, byte[] round1, byte[] round2)
        {
            byte[] possibleBytes = new byte[_curve.FieldSize / 8];
            round1.CopyTo(possibleBytes, 0);
            byte[] trimmedBytes = new byte[ECRng.TrimmedBytes];
            Array.Copy(BitConverter.GetBytes(currentGuess), 0, trimmedBytes, 0, ECRng.TrimmedBytes);
            trimmedBytes.CopyTo(possibleBytes, round1.Length);


            BigInteger possibleX = BigIntegerHelper.FromBytes(possibleBytes);


            if (!_curve.TryGetPoints(possibleX, out (BigPoint, BigPoint) possibleRQs))
            {
                return null;
            }

            BigPoint[] RQs = { possibleRQs.Item1, possibleRQs.Item2 };


            foreach (BigPoint rq in RQs)
            {
                BigPoint rp = _curve.Multiply(rq, e);
                BigInteger s = rp.X;
                ECRng rng = new ECRng(RngParams, s);
                byte[] output = rng.Next();
                if (output.SequenceEqual(round2))
                {
                    return rng;
                }

            }
            return null;
        }

    }

}
