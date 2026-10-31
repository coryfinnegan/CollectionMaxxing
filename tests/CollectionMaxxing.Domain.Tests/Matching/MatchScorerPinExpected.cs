namespace CollectionMaxxing.Domain.Tests.Matching;

/// <summary>What <see cref="MatchScorerPinScenarios.Describe"/> gives for each scenario, as recorded from the scorer.</summary>
internal static class MatchScorerPinExpected
{
    public static readonly IReadOnlyDictionary<string, string> ByScenario = new Dictionary<string, string>
    {
        ["alien 1979 dvd, the title carries the year"] = """
            Matched
            alien score=1 alone=1 signals=2 exactId=False conflict=False terms=[title(0.55)=1, type(0.15)=1]
            aliens score=0.869047619 alone=0.869047619 signals=2 exactId=False conflict=False terms=[title(0.55)=0.8333333333333334, type(0.15)=1]
            """,
        ["alien dvd, the year is read as a year"] = """
            Ambiguous
            alien score=1 alone=1 signals=3 exactId=False conflict=False terms=[title(0.55)=1, type(0.15)=1, year(0.1)=1]
            alien-nodate score=0.9375 alone=0.9375 signals=2 exactId=False conflict=False terms=[title(0.55)=1, type(0.15)=1, year(0.1)=0.5?]
            aliens score=0.760416667 alone=0.760416667 signals=3 exactId=False conflict=False terms=[title(0.55)=0.8333333333333334, type(0.15)=1, year(0.1)=0]
            """,
        ["madden 2005 against its other years and consoles"] = """
            Ambiguous
            m2005 score=1 alone=1 signals=3 exactId=False conflict=False terms=[title(0.4)=1, type(0.05)=1, platform(0.3)=1]
            mnfl score=1 alone=1 signals=3 exactId=False conflict=False terms=[title(0.4)=1, type(0.05)=1, platform(0.3)=1]
            m2004 score=0.84 alone=0.84 signals=3 exactId=False conflict=True terms=[title(0.4)=0.7, type(0.05)=1, platform(0.3)=1]
            m2003 score=0.84 alone=0.84 signals=3 exactId=False conflict=True terms=[title(0.4)=0.7, type(0.05)=1, platform(0.3)=1]
            m2005x score=0.6 alone=0.6 signals=3 exactId=False conflict=True terms=[title(0.4)=1, type(0.05)=1, platform(0.3)=0]
            """,
        ["metroid prime 2 against its subtitle and its predecessor"] = """
            Matched
            prime2 score=1 alone=1 signals=2 exactId=False conflict=False terms=[title(0.35)=1, platform(0.15)=1]
            echoes score=0.86 alone=0.86 signals=2 exactId=False conflict=False terms=[title(0.35)=0.8, platform(0.15)=1]
            prime score=0.755 alone=0.755 signals=2 exactId=False conflict=False terms=[title(0.35)=0.65, platform(0.15)=1]
            prime-long score=0.664 alone=0.664 signals=2 exactId=False conflict=False terms=[title(0.35)=0.52, platform(0.15)=1]
            """,
        ["a title read without its subtitle"] = """
            Matched
            kc-plain score=1 alone=1 signals=2 exactId=False conflict=False terms=[title(0.5)=1, type(0.1)=1]
            kc score=0.833333333 alone=0.833333333 signals=2 exactId=False conflict=False terms=[title(0.5)=0.8, type(0.1)=1]
            kc-other score=0.583333333 alone=0.583333333 signals=2 exactId=False conflict=False terms=[title(0.5)=0.5, type(0.1)=1]
            """,
        ["the same words in another order"] = """
            Matched
            the-kitchen-orchard score=1 alone=1 signals=2 exactId=False conflict=False terms=[title(0.5)=1, type(0.1)=1]
            the-orchard-kitchen score=0.916666667 alone=0.916666667 signals=2 exactId=False conflict=False terms=[title(0.5)=0.9, type(0.1)=1]
            orchard-kitchen-recipes score=0.766666667 alone=0.766666667 signals=2 exactId=False conflict=False terms=[title(0.5)=0.7200000000000001, type(0.1)=1]
            orchard score=0.583333333 alone=0.583333333 signals=2 exactId=False conflict=False terms=[title(0.5)=0.5, type(0.1)=1]
            """,
        ["a spine reads title and author as one line"] = """
            Matched
            dune score=1 alone=1 signals=3 exactId=False conflict=False terms=[title(0.5)=1, type(0.1)=1, creator(0.2)=1]
            chronicles score=0.875 alone=0.875 signals=3 exactId=False conflict=False terms=[title(0.5)=0.8, type(0.1)=1, creator(0.2)=1]
            messiah score=0.6875 alone=0.6875 signals=3 exactId=False conflict=False terms=[title(0.5)=0.5, type(0.1)=1, creator(0.2)=1]
            other score=0.583333333 alone=0.583333333 signals=3 exactId=False conflict=False terms=[title(0.5)=0.3333333333333333, type(0.1)=1, creator(0.2)=1]
            """,
        ["a junk author read hides nothing"] = """
            Ambiguous
            keepers score=1 alone=1 signals=3 exactId=False conflict=False terms=[title(0.5)=1, type(0.1)=1, creator(0.2)=1]
            keepers-two score=1 alone=1 signals=3 exactId=False conflict=False terms=[title(0.5)=1, type(0.1)=1, creator(0.2)=1]
            harbor score=0.791666667 alone=0.791666667 signals=3 exactId=False conflict=False terms=[title(0.5)=0.6666666666666666, type(0.1)=1, creator(0.2)=1]
            harbor-two score=0.352822581 alone=0.352822581 signals=3 exactId=False conflict=False terms=[title(0.5)=0.314516129032258, type(0.1)=1, creator(0.2)=0.125]
            """,
        ["a surname first on the spine"] = """
            Ambiguous
            keepers score=1 alone=1 signals=3 exactId=False conflict=False terms=[title(0.5)=1, type(0.1)=1, creator(0.2)=1]
            keepers-comma score=1 alone=1 signals=3 exactId=False conflict=False terms=[title(0.5)=1, type(0.1)=1, creator(0.2)=1]
            atlas score=0.576612903 alone=0.576612903 signals=3 exactId=False conflict=False terms=[title(0.5)=0.32258064516129037, type(0.1)=1, creator(0.2)=1]
            """,
        ["a book titled after its author"] = """
            Matched
            self score=1 alone=1 signals=2 exactId=False conflict=False terms=[title(0.5)=1, type(0.1)=1]
            dune score=0.294871795 alone=0.294871795 signals=2 exactId=False conflict=False terms=[title(0.5)=0.15384615384615385, type(0.1)=1]
            """,
        ["an author with initials and a suffix"] = """
            Matched
            quest score=0.916666667 alone=0.916666667 signals=3 exactId=False conflict=False terms=[title(0.5)=0.8666666666666667, type(0.1)=1, creator(0.2)=1]
            keep score=0.75 alone=0.75 signals=3 exactId=False conflict=False terms=[title(0.5)=0.6, type(0.1)=1, creator(0.2)=1]
            quest-jr score=0.552272727 alone=0.552272727 signals=3 exactId=False conflict=False terms=[title(0.5)=0.52, type(0.1)=1, creator(0.2)=0.40909090909090906]
            """,
        ["a film read against a show and a game"] = """
            Ambiguous
            matrix score=1 alone=1 signals=2 exactId=False conflict=False terms=[title(0.55)=1, type(0.15)=1]
            matrix-comma score=1 alone=1 signals=2 exactId=False conflict=False terms=[title(0.55)=1, type(0.15)=1]
            matrix-tv score=0.839285714 alone=0.839285714 signals=2 exactId=False conflict=False terms=[title(0.55)=1, type(0.15)=0.25]
            matrix-game score=0.785714286 alone=0.785714286 signals=2 exactId=False conflict=False terms=[title(0.55)=1, type(0.15)=0]
            reloaded score=0.607142857 alone=0.607142857 signals=2 exactId=False conflict=False terms=[title(0.55)=0.5, type(0.15)=1]
            """,
        ["roman numerals and digits"] = """
            Matched
            rocky4 score=1 alone=1 signals=2 exactId=False conflict=False terms=[title(0.35)=1, type(0.1)=1]
            rocky3 score=0.722222222 alone=0.722222222 signals=2 exactId=False conflict=False terms=[title(0.35)=0.6428571428571429, type(0.1)=1]
            rocky20 score=0.659722222 alone=0.659722222 signals=2 exactId=False conflict=False terms=[title(0.35)=0.5625, type(0.1)=1]
            rocky score=0.638888889 alone=0.638888889 signals=2 exactId=False conflict=False terms=[title(0.35)=0.5357142857142857, type(0.1)=1]
            """,
        ["final fantasy numerals on a platform"] = """
            Ambiguous
            ff7 score=1 alone=1 signals=3 exactId=False conflict=False terms=[title(0.4)=1, type(0.05)=1, platform(0.3)=1]
            ff7-nodate score=1 alone=1 signals=3 exactId=False conflict=False terms=[title(0.4)=1, type(0.05)=1, platform(0.3)=1]
            ff8 score=0.84 alone=0.84 signals=3 exactId=False conflict=False terms=[title(0.4)=0.7, type(0.05)=1, platform(0.3)=1]
            ff7-pc score=0.6 alone=0.6 signals=3 exactId=False conflict=True terms=[title(0.4)=1, type(0.05)=1, platform(0.3)=0]
            """,
        ["a title with too few letters"] = """
            Ambiguous
            decade score=1 alone=1 signals=2 exactId=False conflict=False terms=[title(0.5)=1, type(0.1)=1]
            zero score=0.666666667 alone=0.666666667 signals=2 exactId=False conflict=False terms=[title(0.5)=0.6000000000000001, type(0.1)=1]
            walt score=0.308712121 alone=0.308712121 signals=2 exactId=False conflict=False terms=[title(0.5)=0.17045454545454547, type(0.1)=1]
            """,
        ["a short title in caps"] = """
            Ambiguous
            up score=1 alone=1 signals=2 exactId=False conflict=False terms=[title(0.5)=1, type(0.1)=1]
            up-subtitle score=0.833333333 alone=0.833333333 signals=2 exactId=False conflict=False terms=[title(0.5)=0.8, type(0.1)=1]
            upstairs score=0.375 alone=0.375 signals=2 exactId=False conflict=False terms=[title(0.5)=0.25, type(0.1)=1]
            """,
        ["artwork distances for some of the candidates"] = """
            Matched
            m64 score=0.994285714 alone=0.994285714 signals=3 exactId=False conflict=False terms=[title(0.35)=1, platform(0.15)=1, art(0.2)=0.98]
            coverless score=0.714285714 alone=1 signals=2 exactId=False conflict=False terms=[title(0.35)=1, platform(0.15)=1, art(0.2)=0?]
            m64ds score=0.581764706 alone=0.581764706 signals=3 exactId=False conflict=True terms=[title(0.35)=0.8235294117647058, platform(0.15)=0, art(0.2)=0.595]
            sunshine score=0.225 alone=0.225 signals=3 exactId=False conflict=True terms=[title(0.35)=0.44999999999999996, platform(0.15)=0, art(0.2)=0]
            """,
        ["artwork distances that are unusable"] = """
            Ambiguous
            a score=0.690909091 alone=0.8 signals=1 exactId=False conflict=False terms=[title(0.35)=0.8, art(0.2)=0.5?]
            c score=0.548495298 alone=0.548495298 signals=2 exactId=False conflict=False terms=[title(0.35)=0.33620689655172414, art(0.2)=0.9199999999999999]
            b score=0.385454545 alone=0.32 signals=1 exactId=False conflict=False terms=[title(0.35)=0.32000000000000006, art(0.2)=0.5?]
            """,
        ["a loose cartridge with the console on the label"] = """
            Ambiguous
            m64 score=0.786666667 alone=0.786666667 signals=3 exactId=False conflict=False terms=[title(0.4)=0.6, type(0.05)=1, platform(0.3)=1]
            kart score=0.650574713 alone=0.650574713 signals=3 exactId=False conflict=False terms=[title(0.4)=0.3448275862068966, type(0.05)=1, platform(0.3)=1]
            unknown-platform score=0.586666667 alone=0.586666667 signals=2 exactId=False conflict=False terms=[title(0.4)=0.6, type(0.05)=1, platform(0.3)=0.5?]
            m64ds score=0.36091954 alone=0.36091954 signals=3 exactId=False conflict=True terms=[title(0.4)=0.5517241379310345, type(0.05)=1, platform(0.3)=0]
            """,
        ["an edition mark"] = """
            Ambiguous
            pc score=1 alone=1 signals=3 exactId=False conflict=False terms=[title(0.35)=1, platform(0.15)=1, edition(0.1)=1]
            pc-sub score=0.966666667 alone=0.966666667 signals=3 exactId=False conflict=False terms=[title(0.35)=1, platform(0.15)=1, edition(0.1)=0.8]
            plain score=0.916666667 alone=0.916666667 signals=2 exactId=False conflict=False terms=[title(0.35)=1, platform(0.15)=1, edition(0.1)=0.5?]
            gh score=0.880952381 alone=0.880952381 signals=3 exactId=False conflict=False terms=[title(0.35)=1, platform(0.15)=1, edition(0.1)=0.2857142857142857]
            """,
        ["a bare label on a tape"] = """
            Ambiguous
            sac3 score=0.97755102 alone=0.97755102 signals=2 exactId=False conflict=False terms=[title(0.55)=0.9714285714285714, type(0.15)=1]
            sac3-dvd score=0.870408163 alone=0.870408163 signals=2 exactId=False conflict=False terms=[title(0.55)=0.9714285714285714, type(0.15)=0.5]
            sac3-short score=0.573469388 alone=0.573469388 signals=2 exactId=False conflict=False terms=[title(0.55)=0.4571428571428572, type(0.15)=1]
            sac score=0.45 alone=0.45 signals=2 exactId=False conflict=False terms=[title(0.55)=0.30000000000000004, type(0.15)=1]
            """,
        ["accents on either side"] = """
            Ambiguous
            red score=1 alone=1 signals=2 exactId=False conflict=False terms=[title(0.35)=1, platform(0.15)=1]
            red-accent score=1 alone=1 signals=2 exactId=False conflict=False terms=[title(0.35)=1, platform(0.15)=1]
            blue score=0.86 alone=0.86 signals=2 exactId=False conflict=False terms=[title(0.35)=0.8, platform(0.15)=1]
            """,
        ["an identifier is a match whatever the title"] = """
            Matched
            by-id score=1 alone=1 signals=0 exactId=True conflict=False terms=[identifier(1)=1]
            matrix score=0.115384615 alone=0.197802198 signals=1 exactId=False conflict=False terms=[title(0.55)=0.11538461538461542]
            """,
        ["no candidates"] = """
            NotFound
            """,
        ["a read with only whitespace for a title"] = """
            Ambiguous
            matrix score=1 alone=1 signals=1 exactId=False conflict=False terms=[type(0.1)=1]
            alien score=1 alone=1 signals=1 exactId=False conflict=False terms=[type(0.1)=1]
            """,
        ["a release with no title"] = """
            Matched
            matrix score=1 alone=1 signals=2 exactId=False conflict=False terms=[title(0.35)=1, type(0.1)=1]
            blank score=0.222222222 alone=0.222222222 signals=1 exactId=False conflict=False terms=[title(0.35)=0?, type(0.1)=1]
            punctuation score=0.222222222 alone=0.222222222 signals=2 exactId=False conflict=False terms=[title(0.35)=0, type(0.1)=1]
            """,
        ["several authors and a read author who is one of them"] = """
            Ambiguous
            ampersand score=0.935483871 alone=0.935483871 signals=3 exactId=False conflict=False terms=[title(0.5)=1, type(0.1)=1, creator(0.2)=0.7419354838709677]
            two score=0.858695652 alone=0.858695652 signals=3 exactId=False conflict=False terms=[title(0.5)=1, type(0.1)=1, creator(0.2)=0.4347826086956522]
            one score=0.858695652 alone=0.858695652 signals=3 exactId=False conflict=False terms=[title(0.5)=1, type(0.1)=1, creator(0.2)=0.4347826086956522]
            other score=0.84375 alone=0.84375 signals=3 exactId=False conflict=False terms=[title(0.5)=1, type(0.1)=1, creator(0.2)=0.375]
            """,
        ["a repeated word"] = """
            Ambiguous
            camp3 score=1 alone=1 signals=1 exactId=False conflict=False terms=[title(0.35)=1]
            camp score=0.9 alone=0.9 signals=1 exactId=False conflict=False terms=[title(0.35)=0.9]
            camp-iii score=0.375 alone=0.375 signals=1 exactId=False conflict=False terms=[title(0.35)=0.375]
            camp-3 score=0.375 alone=0.375 signals=1 exactId=False conflict=False terms=[title(0.35)=0.375]
            """,
        ["a long title with a colon and an abbreviation"] = """
            Matched
            full score=1 alone=1 signals=2 exactId=False conflict=False terms=[title(0.55)=1, type(0.15)=1]
            short score=0.731932773 alone=0.731932773 signals=2 exactId=False conflict=False terms=[title(0.55)=0.6588235294117647, type(0.15)=1]
            other score=0.399159664 alone=0.399159664 signals=2 exactId=False conflict=False terms=[title(0.55)=0.2352941176470588, type(0.15)=1]
            """,
        ["a title with a subtitle in brackets and a dash"] = """
            Ambiguous
            star score=0.844444444 alone=0.844444444 signals=2 exactId=False conflict=False terms=[title(0.35)=0.8, type(0.1)=1]
            anh-long score=0.827160494 alone=0.827160494 signals=2 exactId=False conflict=False terms=[title(0.35)=0.7777777777777778, type(0.1)=1]
            anh score=0.546296296 alone=0.546296296 signals=2 exactId=False conflict=False terms=[title(0.35)=0.4166666666666667, type(0.1)=1]
            esb score=0.436507937 alone=0.436507937 signals=2 exactId=False conflict=False terms=[title(0.35)=0.2755102040816326, type(0.1)=1]
            """,
        ["years in the title and a read year"] = """
            Ambiguous
            hc2005 score=1 alone=1 signals=3 exactId=False conflict=False terms=[title(0.55)=1, type(0.15)=1, year(0.1)=1]
            hc score=1 alone=1 signals=3 exactId=False conflict=False terms=[title(0.55)=1, type(0.15)=1, year(0.1)=1]
            hc2004 score=0.7625 alone=0.7625 signals=3 exactId=False conflict=True terms=[title(0.55)=0.7, type(0.15)=1, year(0.1)=0.75]
            hc2005-1979 score=0.69921875 alone=0.69921875 signals=3 exactId=False conflict=True terms=[title(0.55)=0.5625, type(0.15)=1, year(0.1)=1]
            """,
        ["year proximity"] = """
            Ambiguous
            j75 score=1 alone=1 signals=3 exactId=False conflict=False terms=[title(0.55)=1, type(0.15)=1, year(0.1)=1]
            j76 score=0.96875 alone=0.96875 signals=3 exactId=False conflict=False terms=[title(0.55)=1, type(0.15)=1, year(0.1)=0.75]
            jn score=0.9375 alone=0.9375 signals=2 exactId=False conflict=False terms=[title(0.55)=1, type(0.15)=1, year(0.1)=0.5?]
            j79 score=0.875 alone=0.875 signals=3 exactId=False conflict=False terms=[title(0.55)=1, type(0.15)=1, year(0.1)=0]
            j2 score=0.5625 alone=0.5625 signals=3 exactId=False conflict=False terms=[title(0.55)=0.5, type(0.15)=1, year(0.1)=0.25]
            """,
        ["one spine against thirty candidates"] = """
            Ambiguous
            c00 score=1 alone=1 signals=4 exactId=False conflict=False terms=[title(0.5)=1, type(0.1)=1, year(0.05)=1, creator(0.2)=1]
            c27 score=1 alone=1 signals=4 exactId=False conflict=False terms=[title(0.5)=1, type(0.1)=1, year(0.05)=1, creator(0.2)=1]
            c25 score=0.970588235 alone=0.970588235 signals=4 exactId=False conflict=False terms=[title(0.5)=1, type(0.1)=1, year(0.05)=0.5, creator(0.2)=1]
            c01 score=0.852941176 alone=0.852941176 signals=3 exactId=False conflict=False terms=[title(0.5)=0.8, type(0.1)=1, year(0.05)=0.5?, creator(0.2)=1]
            c28 score=0.852941176 alone=0.852941176 signals=3 exactId=False conflict=False terms=[title(0.5)=0.8, type(0.1)=1, year(0.05)=0.5?, creator(0.2)=1]
            c29 score=0.852941176 alone=0.852941176 signals=3 exactId=False conflict=False terms=[title(0.5)=0.8, type(0.1)=1, year(0.05)=0.5?, creator(0.2)=1]
            c02 score=0.774509804 alone=0.774509804 signals=3 exactId=False conflict=False terms=[title(0.5)=0.6666666666666666, type(0.1)=1, year(0.05)=0.5?, creator(0.2)=1]
            c26 score=0.735294118 alone=0.735294118 signals=3 exactId=False conflict=False terms=[title(0.5)=0.6, type(0.1)=1, year(0.05)=0.5?, creator(0.2)=1]
            c03 score=0.725490196 alone=0.725490196 signals=4 exactId=False conflict=False terms=[title(0.5)=0.5333333333333333, type(0.1)=1, year(0.05)=1, creator(0.2)=1]
            c23 score=0.676470588 alone=0.676470588 signals=3 exactId=False conflict=False terms=[title(0.5)=0.5, type(0.1)=1, year(0.05)=0.5?, creator(0.2)=1]
            c07 score=0.585972851 alone=0.585972851 signals=3 exactId=False conflict=False terms=[title(0.5)=0.34615384615384615, type(0.1)=1, year(0.05)=0.5?, creator(0.2)=1]
            c08 score=0.552036199 alone=0.552036199 signals=3 exactId=False conflict=False terms=[title(0.5)=0.28846153846153844, type(0.1)=1, year(0.05)=0.5?, creator(0.2)=1]
            c24 score=0.529411765 alone=0.529411765 signals=3 exactId=False conflict=False terms=[title(0.5)=0.4, type(0.1)=1, year(0.05)=1, creator(0.2)=0.5?]
            c20 score=0.511029412 alone=0.511029412 signals=4 exactId=False conflict=False terms=[title(0.5)=0.21875, type(0.1)=1, year(0.05)=0.5, creator(0.2)=1]
            c12 score=0.495798319 alone=0.495798319 signals=4 exactId=False conflict=False terms=[title(0.5)=0.14285714285714285, type(0.1)=1, year(0.05)=1, creator(0.2)=1]
            c11 score=0.494397759 alone=0.494397759 signals=3 exactId=False conflict=False terms=[title(0.5)=0.19047619047619047, type(0.1)=1, year(0.05)=0.5?, creator(0.2)=1]
            c10 score=0.492647059 alone=0.492647059 signals=4 exactId=False conflict=False terms=[title(0.5)=0.1875, type(0.1)=1, year(0.05)=0.5, creator(0.2)=1]
            c06 score=0.481092437 alone=0.481092437 signals=4 exactId=False conflict=False terms=[title(0.5)=0.375, type(0.1)=1, year(0.05)=1, creator(0.2)=0.3571428571428571]
            c04 score=0.430555556 alone=0.430555556 signals=3 exactId=False conflict=False terms=[title(0.5)=0.4375, type(0.1)=1, year(0.05)=0.5?, creator(0.2)=0.11111111111111116]
            c05 score=0.430555556 alone=0.430555556 signals=4 exactId=False conflict=False terms=[title(0.5)=0.4375, type(0.1)=1, year(0.05)=0.5, creator(0.2)=0.11111111111111116]
            c19 score=0.408990011 alone=0.408990011 signals=2 exactId=False conflict=False terms=[title(0.5)=0.24528301886792447, type(0.1)=1, year(0.05)=0.5?, creator(0.2)=0.5?]
            c21 score=0.405405405 alone=0.405405405 signals=3 exactId=False conflict=False terms=[title(0.5)=0.18918918918918914, type(0.1)=1, year(0.05)=1, creator(0.2)=0.5?]
            c22 score=0.403921569 alone=0.403921569 signals=3 exactId=False conflict=False terms=[title(0.5)=0.25, type(0.1)=1, year(0.05)=0.5?, creator(0.2)=0.4666666666666667]
            c09 score=0.395152505 alone=0.395152505 signals=4 exactId=False conflict=False terms=[title(0.5)=0.3125, type(0.1)=1, year(0.05)=1, creator(0.2)=0.14814814814814814]
            c18 score=0.383578431 alone=0.383578431 signals=4 exactId=False conflict=False terms=[title(0.5)=0.21875, type(0.1)=1, year(0.05)=1, creator(0.2)=0.33333333333333337]
            c14 score=0.348133484 alone=0.348133484 signals=3 exactId=False conflict=False terms=[title(0.5)=0.21875, type(0.1)=1, year(0.05)=0.5?, creator(0.2)=0.3076923076923077]
            c15 score=0.346907994 alone=0.346907994 signals=4 exactId=False conflict=False terms=[title(0.5)=0.16666666666666663, type(0.1)=1, year(0.05)=1, creator(0.2)=0.3076923076923077]
            c17 score=0.316993464 alone=0.316993464 signals=3 exactId=False conflict=False terms=[title(0.5)=0.19999999999999996, type(0.1)=1, year(0.05)=0.5?, creator(0.2)=0.2222222222222222]
            c13 score=0.311934389 alone=0.311934389 signals=3 exactId=False conflict=False terms=[title(0.5)=0.21875, type(0.1)=1, year(0.05)=0.5?, creator(0.2)=0.15384615384615385]
            c16 score=0.29125817 alone=0.29125817 signals=3 exactId=False conflict=False terms=[title(0.5)=0.15625, type(0.1)=1, year(0.05)=0.5?, creator(0.2)=0.2222222222222222]
            """,
    };
}
