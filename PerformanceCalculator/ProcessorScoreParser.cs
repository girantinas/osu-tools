﻿// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Game.Beatmaps;
using osu.Game.Rulesets;
using osu.Game.Scoring;
using osu.Game.Scoring.Legacy;

namespace PerformanceCalculator
{
    /// <summary>
    /// A <see cref="LegacyScoreParser"/> which has a predefined beatmap and rulesets.
    /// </summary>
    public class ProcessorScoreParser : LegacyScoreParser
    {
        private readonly WorkingBeatmap beatmap;

        public ProcessorScoreParser(WorkingBeatmap beatmap)
        {
            this.beatmap = beatmap;
        }

        public Score Parse(ScoreInfo scoreInfo)
        {
            var score = new Score { ScoreInfo = scoreInfo };
            CalculateAccuracy(score.ScoreInfo);
            return score;
        }

        protected override Ruleset GetRuleset(int rulesetId) => LegacyHelper.GetRulesetFromLegacyID(rulesetId);

        protected override WorkingBeatmap GetBeatmap(string md5Hash) => beatmap;
    }
}
