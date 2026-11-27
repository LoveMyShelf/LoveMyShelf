using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;

namespace LoveMyShelf {
    internal static class MotionTrack {
        // Time values are forward source seconds relative to the first frame of the chosen part.
        internal static MotionTrackKey[] Follow(IList<byte[]> frames,int width,int height,int start,Rect box,bool followSize=false) {
            return Follow(i=>frames[i],frames.Count,width,height,start,box,followSize,1,false,CancellationToken.None,null);
        }

        internal static MotionTrackKey[] Follow(Func<int,byte[]> read,int count,int width,int height,int start,Rect box,
            bool followSize,double secondsPerFrame,bool forwardOnly,CancellationToken token,Action<int,int> progress) {
            if(count<=0 || width<2 || height<2 || start<0 || start>=count || box.IsEmpty || box.Width<=0 || box.Height<=0)
                throw new ArgumentException("Invalid tracking frames or box.");
            var results=new MotionTrackKey[count];
            int done=0,total=forwardOnly?count-start:count;
            Action next=()=>{done++;if(progress!=null)progress(done,total);};
            results[start]=new MotionTrackKey(start*secondsPerFrame,box,false,false);
            next();
            TrackDirection(read,width,height,start,count,1,box,followSize,results,secondsPerFrame,token,next);
            if(!forwardOnly)TrackDirection(read,width,height,start,-1,-1,box,followSize,results,secondsPerFrame,token,next);
            return results;
        }

        static double Clamp(double value,double min,double max) {return Math.Max(min,Math.Min(max,value));}

        // Weighted ZNCC template matcher (center-weighted + adaptive padding + multi-scale + occlusion recovery)
        static void TrackDirection(Func<int,byte[]> read, int w, int h, int first, int stop, int step,
            Rect box, bool followSize, MotionTrackKey[] results, double secondsPerFrame,
            CancellationToken token, Action progress) {
            if(first+step==stop)return;
            byte[] initial = read(first);
            double initW = box.Width * w;
            double initH = box.Height * h;
            double curCx = (box.X + box.Width * 0.5) * w;
            double curCy = (box.Y + box.Height * 0.5) * h;
            double curW = initW;
            double curH = initH;

            // Check inner texture variance first; only add outer context padding if the target itself is flat/uniform
            double[] probeBuf = new double[16 * 16];
            SamplePatch(initial, w, h, curCx, curCy, curW, curH, 16, 16, probeBuf);
            double innerStd = ComputeStdDev(probeBuf);
            double padFactor = innerStd < 12.0 ? 1.30 : 1.00;

            int tplW = (int)Math.Round(initW * padFactor);
            int tplH = (int)Math.Round(initH * padFactor);
            int maxSide = Math.Max(tplW, tplH);
            if (maxSide > 28) {
                double s = 28.0 / maxSide;
                tplW = Math.Max(10, (int)Math.Round(tplW * s));
                tplH = Math.Max(10, (int)Math.Round(tplH * s));
            } else {
                tplW = Math.Max(10, tplW);
                tplH = Math.Max(10, tplH);
            }

            int tplLen = tplW * tplH;
            double[] weights = BuildCenterWeights(tplW, tplH, padFactor > 1.01 ? 0.65 : 0.20);
            double[] initTpl = new double[tplLen];
            double[] currTpl = new double[tplLen];
            double[] patchBuf = new double[tplLen];

            SamplePatch(initial, w, h, curCx, curCy, curW * padFactor, curH * padFactor, tplW, tplH, initTpl);
            Array.Copy(initTpl, currTpl, tplLen);

            double initNorm;
            double[] initZero = NormalizeTemplateWeighted(initTpl, weights, out initNorm);
            double currNorm;
            double[] currZero = NormalizeTemplateWeighted(currTpl, weights, out currNorm);

            double vx = 0.0, vy = 0.0;
            int lostStreak = 0;

            double[] scales = followSize ? new[] { 0.92, 0.96, 1.00, 1.04, 1.08 } : new[] { 1.00 };
            double minW = Math.Max(6.0, initW * 0.35);
            double maxW = Math.Max(initW,Math.Min(w * 0.95, initW * 2.5));
            double minH = Math.Max(6.0, initH * 0.35);
            double maxH = Math.Max(initH,Math.Min(h * 0.95, initH * 2.5));

            for (int f = first+step; f != stop; f+=step) {
                token.ThrowIfCancellationRequested();
                byte[] img = read(f);
                double predCx = Clamp(curCx + vx * 0.6, 0, w - 1);
                double predCy = Clamp(curCy + vy * 0.6, 0, h - 1);

                double baseR = Math.Max(22.0, Math.Max(curW, curH) * 0.85);
                double searchR = lostStreak == 0 ? baseR : Math.Min(Math.Max(w, h) * 0.5, baseR * (1.5 + 0.5 * Math.Min(lostStreak, 5)));
                int coarseStep = lostStreak == 0 ? 2 : 3;

                double bestCx = predCx;
                double bestCy = predCy;
                double bestW = curW;
                double bestH = curH;
                double bestScore = -2.0;

                // Stage 1: Coarse search at current scale
                int minX = Math.Max(0, (int)Math.Floor(predCx - searchR));
                int maxX = Math.Min(w - 1, (int)Math.Ceiling(predCx + searchR));
                int minY = Math.Max(0, (int)Math.Floor(predCy - searchR));
                int maxY = Math.Min(h - 1, (int)Math.Ceiling(predCy + searchR));

                for (int cy = minY; cy <= maxY; cy += coarseStep) {
                    token.ThrowIfCancellationRequested();
                    for (int cx = minX; cx <= maxX; cx += coarseStep) {
                        SamplePatch(img, w, h, cx, cy, curW * padFactor, curH * padFactor, tplW, tplH, patchBuf);
                        double sCurr = ComputeScoreWeighted(patchBuf, currTpl, currZero, currNorm, weights);
                        double sInit = ComputeScoreWeighted(patchBuf, initTpl, initZero, initNorm, weights);
                        double sc = Math.Max(0.60 * sCurr + 0.40 * sInit, sInit);
                        if (sc > bestScore) {
                            bestScore = sc;
                            bestCx = cx;
                            bestCy = cy;
                        }
                    }
                }

                // If lost or local confidence is low, scan the whole frame sparsely with init template to re-acquire
                bool usedGlobal = false;
                if (lostStreak >= 1 || bestScore < 0.62) {
                    int globalStep = Math.Max(4, (int)Math.Round(Math.Min(curW, curH) * 0.22));
                    double globalBest = bestScore;
                    double gx = bestCx, gy = bestCy;
                    for (int cy = globalStep / 2; cy < h; cy += globalStep) {
                        token.ThrowIfCancellationRequested();
                        for (int cx = globalStep / 2; cx < w; cx += globalStep) {
                            SamplePatch(img, w, h, cx, cy, curW * padFactor, curH * padFactor, tplW, tplH, patchBuf);
                            double sInit = ComputeScoreWeighted(patchBuf, initTpl, initZero, initNorm, weights);
                            if (sInit > globalBest) {
                                globalBest = sInit;
                                gx = cx;
                                gy = cy;
                            }
                        }
                    }
                    // Only jump to a distant global candidate if it is strongly better
                    if (globalBest >= 0.72 && globalBest > bestScore + 0.05) {
                        bestScore = globalBest;
                        bestCx = gx;
                        bestCy = gy;
                        usedGlobal = true;
                    }
                }

                // Stage 2: Fine position + scale search around coarse best
                double fineCenterX = bestCx;
                double fineCenterY = bestCy;
                double bestFinalScore = -2.0;
                double bestRawScore = -2.0;
                double bestInitScore = -2.0;

                int fineRadius = coarseStep + 1;
                for (int si = 0; si < scales.Length; si++) {
                    double scale = scales[si];
                    double candW = followSize ? Clamp(curW * scale, minW, maxW) : initW;
                    double candH = followSize ? Clamp(curH * scale, minH, maxH) : initH;
                    double scalePenalty = Math.Abs(scale - 1.0) * 0.08;

                    int fx0 = Math.Max(0, (int)Math.Floor(fineCenterX - fineRadius));
                    int fx1 = Math.Min(w - 1, (int)Math.Ceiling(fineCenterX + fineRadius));
                    int fy0 = Math.Max(0, (int)Math.Floor(fineCenterY - fineRadius));
                    int fy1 = Math.Min(h - 1, (int)Math.Ceiling(fineCenterY + fineRadius));

                    for (int cy = fy0; cy <= fy1; cy++) {
                        for (int cx = fx0; cx <= fx1; cx++) {
                            SamplePatch(img, w, h, cx, cy, candW * padFactor, candH * padFactor, tplW, tplH, patchBuf);
                            double sCurr = ComputeScoreWeighted(patchBuf, currTpl, currZero, currNorm, weights);
                            double sInit = ComputeScoreWeighted(patchBuf, initTpl, initZero, initNorm, weights);
                            double raw = Math.Max(0.60 * sCurr + 0.40 * sInit, sInit);
                            double penalized = raw - scalePenalty;
                            if (penalized > bestFinalScore) {
                                bestFinalScore = penalized;
                                bestRawScore = raw;
                                bestInitScore = sInit;
                                bestCx = cx;
                                bestCy = cy;
                                bestW = candW;
                                bestH = candH;
                            }
                        }
                    }
                }

                double acceptThreshold = (lostStreak > 0 || usedGlobal) ? 0.74 : 0.62;
                bool isLost = bestRawScore < acceptThreshold;

                if (!isLost) {
                    if (lostStreak > 0) {
                        vx = 0.0;
                        vy = 0.0;
                    } else {
                        vx = bestCx - curCx;
                        vy = bestCy - curCy;
                    }
                    curCx = bestCx;
                    curCy = bestCy;
                    curW = bestW;
                    curH = bestH;
                    lostStreak = 0;

                    // Slow template update only when confident and consistent with initial template
                    if (bestRawScore >= 0.78 && bestInitScore >= 0.60) {
                        SamplePatch(img, w, h, curCx, curCy, curW * padFactor, curH * padFactor, tplW, tplH, patchBuf);
                        const double Alpha = 0.06;
                        for (int i = 0; i < tplLen; i++) {
                            currTpl[i] = (1.0 - Alpha) * currTpl[i] + Alpha * patchBuf[i];
                        }
                        currZero = NormalizeTemplateWeighted(currTpl, weights, out currNorm);
                    }
                } else {
                    // Occluded or lost: hold last known box and damp velocity
                    lostStreak++;
                    vx *= 0.2;
                    vy *= 0.2;
                }

                double normW = followSize ? Clamp(curW / w, 0, 1) : box.Width;
                double normH = followSize ? Clamp(curH / h, 0, 1) : box.Height;
                double normX = Clamp((curCx - curW * 0.5) / w, 0.0, 1.0 - normW);
                double normY = Clamp((curCy - curH * 0.5) / h, 0.0, 1.0 - normH);

                results[f] = new MotionTrackKey(f * secondsPerFrame,new Rect(normX,normY,normW,normH),false,isLost);
                progress();
            }
        }

        static double ComputeStdDev(double[] arr) {
            double sum = 0.0;
            for (int i = 0; i < arr.Length; i++) sum += arr[i];
            double mean = sum / arr.Length;
            double sq = 0.0;
            for (int i = 0; i < arr.Length; i++) {
                double d = arr[i] - mean;
                sq += d * d;
            }
            return Math.Sqrt(sq / arr.Length);
        }

        static double[] BuildCenterWeights(int tplW, int tplH, double edgeWeight) {
            double[] w = new double[tplW * tplH];
            double cx = (tplW - 1) * 0.5;
            double cy = (tplH - 1) * 0.5;
            double rx = Math.Max(1.0, tplW * 0.5);
            double ry = Math.Max(1.0, tplH * 0.5);
            double wSum = 0.0;
            int idx = 0;
            for (int y = 0; y < tplH; y++) {
                double ny = (y - cy) / ry;
                for (int x = 0; x < tplW; x++) {
                    double nx = (x - cx) / rx;
                    double r2 = nx * nx + ny * ny;
                    // Smooth center emphasis: 1.0 at center, down to edgeWeight near corners
                    double val = edgeWeight + (1.0 - edgeWeight) * Math.Exp(-1.8 * r2);
                    w[idx++] = val;
                    wSum += val;
                }
            }
            for (int i = 0; i < w.Length; i++) w[i] /= wSum;
            return w;
        }

        static void SamplePatch(byte[] img, int imgW, int imgH, double cx, double cy, double boxW, double boxH, int tplW, int tplH, double[] dst) {
            double left = cx - boxW * 0.5;
            double top = cy - boxH * 0.5;
            double stepX = boxW / tplW;
            double stepY = boxH / tplH;
            int idx = 0;
            for (int py = 0; py < tplH; py++) {
                double sy = top + (py + 0.5) * stepY;
                if (sy < 0) sy = 0;
                else if (sy > imgH - 1.0001) sy = imgH - 1.0001;
                int y0 = (int)sy;
                int y1 = y0 + 1;
                if (y1 >= imgH) y1 = imgH - 1;
                double fy = sy - y0;
                int row0 = y0 * imgW;
                int row1 = y1 * imgW;

                for (int px = 0; px < tplW; px++) {
                    double sx = left + (px + 0.5) * stepX;
                    if (sx < 0) sx = 0;
                    else if (sx > imgW - 1.0001) sx = imgW - 1.0001;
                    int x0 = (int)sx;
                    int x1 = x0 + 1;
                    if (x1 >= imgW) x1 = imgW - 1;
                    double fx = sx - x0;

                    double v00 = img[row0 + x0];
                    double v10 = img[row0 + x1];
                    double v01 = img[row1 + x0];
                    double v11 = img[row1 + x1];
                    double v0 = v00 + (v10 - v00) * fx;
                    double v1 = v01 + (v11 - v01) * fx;
                    dst[idx++] = v0 + (v1 - v0) * fy;
                }
            }
        }

        static double[] NormalizeTemplateWeighted(double[] tpl, double[] weights, out double norm) {
            int n = tpl.Length;
            double mean = 0.0;
            for (int i = 0; i < n; i++) mean += weights[i] * tpl[i];
            double[] zero = new double[n];
            double sq = 0.0;
            for (int i = 0; i < n; i++) {
                double d = tpl[i] - mean;
                zero[i] = d;
                sq += weights[i] * d * d;
            }
            norm = Math.Sqrt(sq);
            return zero;
        }

        static double ComputeScoreWeighted(double[] patch, double[] rawTpl, double[] zeroTpl, double tplNorm, double[] weights) {
            int n = patch.Length;
            if (tplNorm < 1e-3) {
                double sad = 0.0;
                for (int i = 0; i < n; i++) sad += weights[i] * Math.Abs(patch[i] - rawTpl[i]);
                return 1.0 - (sad / 255.0);
            }

            double mean = 0.0;
            for (int i = 0; i < n; i++) mean += weights[i] * patch[i];

            double dot = 0.0;
            double sq = 0.0;
            for (int i = 0; i < n; i++) {
                double w = weights[i];
                double d = patch[i] - mean;
                dot += w * d * zeroTpl[i];
                sq += w * d * d;
            }
            if (sq < 1e-3) return -1.0;
            return dot / (Math.Sqrt(sq) * tplNorm);
        }
    }
}
