namespace MediaServer.Api.Probe;

/// <summary>
/// The three-letter language tags this library uses, and the normalization onto them.
/// <para>
/// Two callers with opposite needs share it. The <b>probe</b> records whatever a container says and must
/// never drop a language for being unrecognized — an odd tag is still what the file claims. An <b>operator
/// typing one</b> is the opposite: a value nobody recognizes is a typo, and storing it would put a track
/// beyond every "pick my language" control that exists. So <see cref="Normalize"/> answers null for an
/// unknown tag and each caller decides what that means.
/// </para>
/// <para>
/// Codes are the ISO 639-2 <b>bibliographic</b> forms — <c>ger</c>, not <c>deu</c> — because that is the
/// Matroska convention this library already stores and that <c>AudioTrackLabeler</c> infers. The
/// terminological forms are accepted as input and folded onto them, as are ISO 639-1 pairs, so <c>de</c>,
/// <c>deu</c> and <c>ger</c> cannot become three spellings of German in one library.
/// </para>
/// <para>
/// The set was generated from the .NET runtime's own neutral cultures rather than typed by hand, then
/// committed: enumerating ICU at runtime would make the accepted set depend on the host's ICU build, so a
/// tag could be taken on a developer's machine and refused in the container. It is therefore what ICU knew
/// — broad enough for anything a release carries, but neither the full ISO 639-2 register nor free of ISO
/// 639-3 codes ICU also lists. A language missing from it is a one-word addition here.
/// </para>
/// </summary>
internal static class LanguageTags
{
    // Bibliographic three-letter tags, space-separated. Data, not prose — see the generation note above.
    private const string Canonical =
        "afr agq ain aka alb amh apw ara arm arn asa asm ast aze bak bam baq bas bel bem ben ber bez bgc bho blo " +
        "bos bre brx bua bul bur byn cat ccp ceb cgg che chi cho chr chv cic ckb cor cos cst csw cze dan dav div " +
        "dje doi dsb dua dut dyo dzo ebu eng epo est ewe ewo fao fil fin fre fry ful fur gaa geo ger gez gla gle " +
        "glg glv gre grn gsw guj guz hat hau haw hch heb hin hmn hrv hsb hun ibo ice ido iii iku ile ina ind inh " +
        "isc ita ivl jav jbo jgo jmc jpn kab kaj kal kam kan kas kaz kcg kde kea kgp khm khq kik kin kir kkj kln " +
        "kok kor kpe ksb ksf ksh kur kxv lag lao lav lij lin lit lkt lmo lrc ltz lub lug luo lut luy mac mai mal " +
        "mao mar mas may mer mfe mgh mgo mic mid mlg mlt mni moh mon mua mus myv mzn naq nav nbl nde nds nep nez " +
        "nmg nnh nno nnp nob nor nqo nso nus nya nyn oci ori orm osa oss pan pcm per pms pol por pqm prg pus que " +
        "raj rej rhg rof roh rum run rus rwk sag sah san saq sat sbp scn seh ses shi shn shp sin sjd sje sju slo " +
        "slv sme smn smo sna snd som sot spa srd srp ssw sun swa swe syr szl tam tat tel teo tgk tha tib tig tir " +
        "tok ton trv tsn tso tuk tur twq tyv tzm uig ukr urd uzb vai vec ven vie vmw vun wae wal wel wln wol xho " +
        "xnr xog yav yid yor yrl yue zgh zha zul";

    // ISO 639-2/T spellings folded onto the bibliographic form this library stores. The only twenty tags
    // where the two standards disagree; every other code is the same in both.
    private const string TerminologicalAliases =
        "bod:tib ces:cze cym:wel deu:ger ell:gre eus:baq fas:per fra:fre hye:arm isl:ice kat:geo mkd:mac mri:mao " +
        "msa:may mya:bur nld:dut ron:rum slk:slo sqi:alb zho:chi";

    // ISO 639-1 pairs, already mapped onto their bibliographic three-letter form. Matroska's newer
    // LanguageBCP47 element yields "ru" where the legacy element and ffprobe both say "rus".
    private const string TwoLetterPairs =
        "af:afr ak:aka am:amh ar:ara as:asm az:aze ba:bak be:bel bg:bul bm:bam bn:ben bo:tib br:bre bs:bos ca:cat " +
        "ce:che co:cos cs:cze cv:chv cy:wel da:dan de:ger dv:div dz:dzo ee:ewe el:gre en:eng eo:epo es:spa et:est " +
        "eu:baq fa:per ff:ful fi:fin fo:fao fr:fre fy:fry ga:gle gd:gla gl:glg gn:grn gu:guj gv:glv ha:hau he:heb " +
        "hi:hin hr:hrv ht:hat hu:hun hy:arm ia:ina id:ind ie:ile ig:ibo ii:iii io:ido is:ice it:ita iu:iku iv:ivl " +
        "ja:jpn jv:jav ka:geo ki:kik kk:kaz kl:kal km:khm kn:kan ko:kor ks:kas ku:kur kw:cor ky:kir lb:ltz lg:lug " +
        "ln:lin lo:lao lt:lit lu:lub lv:lav mg:mlg mi:mao mk:mac ml:mal mn:mon mr:mar ms:may mt:mlt my:bur nb:nob " +
        "nd:nde ne:nep nl:dut nn:nno no:nor nr:nbl nv:nav ny:nya oc:oci om:orm or:ori os:oss pa:pan pl:pol ps:pus " +
        "pt:por qu:que rm:roh rn:run ro:rum ru:rus rw:kin sa:san sc:srd sd:snd se:sme sg:sag si:sin sk:slo sl:slv " +
        "sm:smo sn:sna so:som sq:alb sr:srp ss:ssw st:sot su:sun sv:swe sw:swa ta:tam te:tel tg:tgk th:tha ti:tir " +
        "tk:tuk tn:tsn to:ton tr:tur ts:tso tt:tat ug:uig uk:ukr ur:urd uz:uzb ve:ven vi:vie wa:wln wo:wol xh:xho " +
        "yi:yid yo:yor za:zha zh:chi zu:zul";

    private static readonly HashSet<string> Recognized =
        new(Canonical.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, string> Aliases = Pairs(TerminologicalAliases, TwoLetterPairs);

    private static Dictionary<string, string> Pairs(params string[] sources)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in sources.SelectMany(source => source.Split(' ', StringSplitOptions.RemoveEmptyEntries)))
        {
            var split = pair.Split(':');
            map[split[0]] = split[1];
        }

        return map;
    }

    /// <summary>Every tag a track may be stored with, ordered — what the language field offers and validates
    /// against, so the app and its UI cannot disagree about which languages exist.</summary>
    public static IReadOnlyList<string> All { get; } = [.. Recognized.Order(StringComparer.Ordinal)];

    /// <summary>
    /// The stored form of a language tag, or null when it is not one this library knows. Accepts the
    /// bibliographic form, the terminological form and the ISO 639-1 pair, and drops a BCP-47 region or
    /// script subtag ("pt-BR") — the library stores the language, and keeping the subtag would make the same
    /// dub sort differently depending on its muxer.
    /// </summary>
    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var primary = raw.Trim().Split('-', '_')[0];
        if (primary.Length == 0)
        {
            return null;
        }

        if (Aliases.TryGetValue(primary, out var mapped))
        {
            return mapped;
        }

        return Recognized.TryGetValue(primary, out var canonical) ? canonical : null;
    }
}
