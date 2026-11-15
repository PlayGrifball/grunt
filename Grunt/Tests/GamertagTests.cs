using Surprenant.Grunt.Util;

namespace Tests;

/// <summary>Gamertag.IsPossible: the names worth asking Xbox about.</summary>
public class GamertagTests
{
    [TestCase("Old Tag")]
    [TestCase("Brotherly690")]
    [TestCase("ABCDEFGHIJKLMNO", Description = "15 characters: the classic limit")]
    [TestCase("Major Nelson#881", Description = "a modern gamertag with its suffix")]
    [TestCase("O'Neill")]
    [TestCase("Smith, J", Description = "classic gamertags allow a comma")]
    [TestCase("øÜêþЯЂў", Description = "modern gamertags allow other scripts")]
    public void Possible(string name) => Assert.That(Gamertag.IsPossible(name), Is.True);

    [TestCase("Bowdery(1)(G)")]
    [TestCase("TBD 357(1)(G)")]
    [TestCase("FatalLeeScotGuest(2)")]
    [TestCase("Lord xPathos(G)")]
    [TestCase("Dr:Who")]
    [TestCase("a_b")]
    [TestCase("x.y")]
    [TestCase("ABCDEFGHIJKLMNOP", Description = "16 characters with no suffix")]
    [TestCase("ABCDEFGHIJKL#1234", Description = "17 with the suffix")]
    [TestCase("Name#")]
    [TestCase("Name#12a")]
    [TestCase("#1234")]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase(null)]
    public void NotPossible(string? name) => Assert.That(Gamertag.IsPossible(name), Is.False);
}
