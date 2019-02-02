// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using Alba.CsConsoleFormat;
using JetBrains.Annotations;
using McMaster.Extensions.CommandLineUtils;
using osu.Framework.IO.Network;
using osu.Game.Beatmaps.Legacy;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Scoring;
using osu.Game.Scoring;

namespace PerformanceCalculator.Profile
{
    [Command(Name = "profile", Description = "Computes the total performance (pp) of a profile.")]
    public class ProfileCommand : ProcessorCommand
    {
        [UsedImplicitly]
        [Required]
        [Argument(0, Name = "user", Description = "User ID is preferred, but username should also work.")]
        public string ProfileName { get; }

        [UsedImplicitly]
        [Required]
        [Argument(1, Name = "api key", Description = "API Key, which you can get from here: https://osu.ppy.sh/p/api")]
        public string Key { get; }

        [UsedImplicitly]
        [Option(Template = "-r|--ruleset:<ruleset-id>", Description = "The ruleset to compute the profile for. 0 - osu!, 1 - osu!taiko, 2 - osu!catch, 3 - osu!mania. Defaults to osu!.")]
        [AllowedValues("0", "1", "2", "3")]
        public int? Ruleset { get; }

        private const string base_url = "https://osu.ppy.sh";

        public override void Execute()
        {
            var displayPlays = new List<UserPlayInfo>();

            var ruleset = LegacyHelper.GetRulesetFromLegacyID(Ruleset ?? 0);

            Console.WriteLine("Getting user data...");
            dynamic userData = getJsonFromApi($"get_user?k={Key}&u={ProfileName}&m={Ruleset}&type=username")[0];

            Console.WriteLine("Getting user top scores...");
            foreach (var play in getJsonFromApi($"get_user_best?k={Key}&u={ProfileName}&m={Ruleset}&limit=100&type=username"))
            {
                string beatmapID = play.beatmap_id;

                string cachePath = Path.Combine("cache", $"{beatmapID}.osu");
                if (!File.Exists(cachePath))
                {
                    Console.WriteLine($"Downloading {beatmapID}.osu...");
                    new FileWebRequest(cachePath, $"{base_url}/osu/{beatmapID}").Perform();
                }

                Mod[] mods = ruleset.ConvertLegacyMods((LegacyMods)play.enabled_mods).ToArray();

                var working = new ProcessorWorkingBeatmap(cachePath, (int)play.beatmap_id) { Mods = { Value = mods } };

                var score = new ProcessorScoreParser(working).Parse(new ScoreInfo
                {
                    Ruleset = ruleset.RulesetInfo,
                    MaxCombo = play.maxcombo,
                    Mods = mods,
                    Statistics = new Dictionary<HitResult, int>
                    {
                        { HitResult.Perfect, (int)play.countgeki },
                        { HitResult.Great, (int)play.count300 },
                        { HitResult.Good, (int)play.count100 },
                        { HitResult.Ok, (int)play.countkatu },
                        { HitResult.Meh, (int)play.count50 },
                        { HitResult.Miss, (int)play.countmiss }
                    }
                });

                var thisPlay = new UserPlayInfo
                {
                    Beatmap = working.BeatmapInfo,
                    LocalPP = ruleset.CreatePerformanceCalculator(working, score.ScoreInfo).Calculate(),
                    LivePP = play.pp,
                    Mods = mods.Length > 0 ? mods.Select(m => m.Acronym).Aggregate((c, n) => $"{c}, {n}") : "None"
                };

                displayPlays.Add(thisPlay);
            }

            var localOrdered = displayPlays.OrderByDescending(p => p.LocalPP).ToList();
            var liveOrdered = displayPlays.OrderByDescending(p => p.LivePP).ToList();

            int index = 0;
            double totalLocalPP = localOrdered.Sum(play => Math.Pow(0.95, index++) * play.LocalPP) + extrapolatePP(localOrdered, "local", (int)userData.playcount);
            double totalLivePP = userData.pp_raw;

            index = 0;
            double nonBonusLivePP = liveOrdered.Sum(play => Math.Pow(0.95, index++) * play.LivePP) + extrapolatePP(liveOrdered, "live", (int)userData.playcount);

            //todo: implement properly. this is pretty damn wrong.
            var playcountBonusPP = (totalLivePP - nonBonusLivePP);
            totalLocalPP += playcountBonusPP;

            OutputDocument(new Document(
                new Span($"User:     {userData.username}"), "\n",
                new Span($"Live PP:  {totalLivePP:F1} (including {playcountBonusPP:F1}pp from playcount)"), "\n",
                new Span($"Local PP: {totalLocalPP:F1}"), "\n",
                new Grid
                {
                    Columns = { GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto },
                    Children =
                    {
                        new Cell("beatmap"),
                        new Cell("live pp"),
                        new Cell("local pp"),
                        new Cell("pp change"),
                        new Cell("position change"),
                        localOrdered.Select(item => new[]
                        {
                            new Cell($"{item.Beatmap.OnlineBeatmapID} - {item.Beatmap}"),
                            new Cell($"{item.LivePP:F1}") { Align = Align.Right },
                            new Cell($"{item.LocalPP:F1}") { Align = Align.Right },
                            new Cell($"{item.LocalPP - item.LivePP:F1}") { Align = Align.Right },
                            new Cell($"{liveOrdered.IndexOf(item) - localOrdered.IndexOf(item):+0;-0;-}") { Align = Align.Center },
                        })
                    }
                }
            ));
        }

        private dynamic getJsonFromApi(string request)
        {
            var req = new JsonWebRequest<dynamic>($"{base_url}/api/{request}");
            req.Perform();
            return req.ResponseObject;
        }

        private double extrapolatePP(List<UserPlayInfo> ppList, string ppStatus, int playcount)
        {
            if (ppList.Count < 100)
                return 0;

            double[] b = gaussNewtonReciprocalFit(ppList, ppStatus);
            double extraPP = 0;

            for (var i = ppList.Count + 1; i < playcount; i++)
            {
                // double predictedPP = (b[0] + b[1] * i) * Math.Pow(0.95, i);
                double predictedPP = (b[0] + b[1] / i) * Math.Pow(0.95, i);

                if (predictedPP < 0)
                {
                    break;
                }

                extraPP += predictedPP;
            }
            return extraPP;
        }


        private double[] linearFit(List<UserPlayInfo> list, string ppStatus)
        {
            double xBar = 0;
            double yBar = 0;
            double N = list.Count;

            if (ppStatus == "live")
                yBar = list.Sum(play => play.LivePP) / N;
            else if (ppStatus == "local")
                yBar = list.Sum(play => play.LocalPP) / N;

            xBar = (N - 1) / 2;

            double Sxy = 0;
            double Sx2 = 0;

            double n = 0;
            if (ppStatus == "live")
                Sxy = list.Sum(play => (n++ - xBar) * (play.LivePP - yBar)) / N;
            else if (ppStatus == "local")
                Sxy = list.Sum(play => (n++ - xBar) * (play.LocalPP - yBar)) / N;

            n = 0;
            Sx2 = list.Sum(play => Math.Pow(n++ - xBar, 2)) / N;

            double slope = Sxy / Sx2;
            double[] coefficients = { yBar - slope * xBar, slope };
            return coefficients;
        }

        private double[] reciprocalFit(List<UserPlayInfo> list, string ppStatus)
        {
            // let z be 1/x, this constructs a linear map from z to y, which in turn is a reciprocal map from x to y
            double zBar = 0;
            double yBar = 0;
            double N = list.Count;

            if (ppStatus == "live")
                yBar = list.Sum(play => play.LivePP) / N;
            else if (ppStatus == "local")
                yBar = list.Sum(play => play.LocalPP) / N;

            double n = 1;
            zBar = list.Sum(play => 1 / (n++)) / N;

            double Syz = 0;
            double Sz2 = 0;

            n = 1;
            if (ppStatus == "live")
                Syz = list.Sum(play => (1 / (n++) - zBar) * (play.LivePP - yBar)) / N;
            else if (ppStatus == "local")
                Syz = list.Sum(play => (1 / (n++) - zBar) * (play.LocalPP - yBar)) / N;

            n = 1;
            Sz2 = list.Sum(play => Math.Pow(1 / (n++) - zBar, 2)) / N;

            double slope = Syz / Sz2;
            double[] coefficients = { yBar - slope * zBar, slope };
            return coefficients;
        }

        // This fit uses the Gauss-Newton Method to Fit a b0 + b1/x curve; see https://en.wikipedia.org/wiki/Gauss%E2%80%93Newton_algorithm
        // The residual of a fit is r_n = y_n - (b0 + b1/x_n)
        // In this case x_n = n, the index of our pp value (top score is 1, 2nd is 2, etc)
        // y_n is the pp value of the nth score
        // r_n = pp_n - (b0 + b1/n)
        // The formula for recursion is given by b = b - (J * JT)^(-1) * JT * r
        // where J represents the Jacobian, JT the transpose of that, r the column vector of all residuals, and b the column vector of all constants
        private double[] gaussNewtonReciprocalFit(List<UserPlayInfo> list, string ppStatus)
        {
            int N = list.Count;

            //Initial guess; normal 1/x curve shifted up so x=1 is at the pp value
            double[] b = { 0, 1 };
            if (ppStatus == "live")
                b[0] = list[0].LivePP;
            else if (ppStatus == "local")
                b[0] = list[0].LocalPP;

            // Each row of the Jacobian is the partial of residual[row] with respect to c[column]. Partial(b[0]) = -1; Partial(b[1]) = -1/n, where n is the row number
            // Jacobian is { { -1, -1/1 }, { -1, -1/2 }, ... { -1, -1/N } }. Its transpose is simply the operation J[i,j] -> JT[j,i]
            // The product of JT and J is given as { { N, Sum of Reciprocals to N }, { Sum of Reciprocals to N, Sum of Reciprocals squared to N } }
            double[,] product = new double[2, 2];
            product[0, 0] = N;
            double n = 1;
            product[0, 1] = list.Sum(p => Math.Pow(n++, -1));
            product[1, 0] = product[0, 1];
            n = 1;
            product[1, 1] = list.Sum(p => Math.Pow(n++, -2));

            //Next take the inverse of the product matrix
            double det = product[0, 0] * product[1, 1] - product[0, 1] * product[1, 0];
            double[,] inverseOfProduct = { { product[1, 1] / det, -product[0, 1] / det }, { -product[1, 0] / det, product[0, 0] / det } };

            double[,] newProduct = new double[2, N];

            //Finally, multiply by the transpose one more time
            for (var i = 0; i < N; i++)
            {
                newProduct[0, i] = -(inverseOfProduct[0, 0] + inverseOfProduct[0, 1] / (i + 1));
                newProduct[1, i] = -(inverseOfProduct[1, 0] + inverseOfProduct[1, 1] / (i + 1));
            }

            //Iterate to get better and better values for constants; note the Jacobian is not a function of the residuals so it can stay out of the loop.
            for (var iterations = 0; iterations < 1000; iterations++)
            {
                //residual calculation
                double[] residuals = new double[100];
                for (var i = 0; i < N; i++)
                {
                    if (ppStatus == "live")
                        residuals[i] = list[i].LivePP - b[0] - b[1] / (i + 1);
                    else if (ppStatus == "local")
                        residuals[i] = list[i].LocalPP - b[0] - b[1] / (i + 1);
                }

                //deviations from residuals
                double[] deltab = { 0, 0 };
                for (var i = 0; i < N; i++)
                {
                    deltab[0] += residuals[i] * newProduct[0, i];
                    deltab[1] += residuals[i] * newProduct[1, i];
                }
                //constants are adjusted by the deviations
                b[0] -= deltab[0];
                b[1] -= deltab[1];
            }

            return b;
        }
    }
}
