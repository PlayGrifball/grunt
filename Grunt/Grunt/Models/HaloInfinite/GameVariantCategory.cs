// <copyright file="MatchInfo.cs" company="Den Delimarsky">
// Developed by Den Delimarsky.
// Den Delimarsky licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.
// The underlying API powering Grunt is managed by 343 Industries and Microsoft. This wrapper is not endorsed by 343 Industries or Microsoft.
// </copyright>

namespace Surprenant.Grunt.Models.HaloInfinite;

/// <summary>
/// Category a game variant falls into.
/// </summary>
public enum GameVariantCategory
{
    MultiplayerSlayer = 6,
    MultiplayerAttrition = 7,
    MultiplayerFiesta = 9,
    MultiplayerStrongholds = 11,
    MultiplayerBastion = 12,
    MultiplayerCtf = 15,
    MultiplayerOddball = 18,
    MultiplayerInfection = 22,
    MultiplayerGrifball = 25,
    MultiplayerLandGrab = 39,
    Unknown = -1,
    None = 0,
    Campaign = 1,
    Forge = 2,
    Academy = 3,
    AcademyTutorial = 4,
    AcademyPractice = 5,
    MultiplayerElimination = 8,
    MultiplayerSwat = 10,
    MultiplayerKingOfTheHill = 13,
    MultiplayerTotalControl = 14,
    MultiplayerAssault = 16,
    MultiplayerExtraction = 17,
    MultiplayerStockpile = 19,
    MultiplayerJuggernaut = 20,
    MultiplayerRegicide = 21,
    MultiplayerEscort = 23,
    MultiplayerGunGame = 24,
    MultiplayerRace = 26,
    MultiplayerPrototype = 27,
    Test = 28,
    TestAcademy = 29,
    TestAudio = 30,
    TestCampaign = 31,
    TestEngine = 32,
    TestForge = 33,
    TestGraphics = 34,
    TestMultiplayer = 35,
    TestSandbox = 36,
    AcademyTraining = 37,
    AcademyWeaponDrill = 38,
    MultiplayerMinigame = 41,
    MultiplayerFirefight = 42,
}