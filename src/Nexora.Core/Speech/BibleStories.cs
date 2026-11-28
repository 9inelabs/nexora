using Nexora.Core.Bible;

namespace Nexora.Core.Speech;

/// <summary>A well-known Bible story or passage, with the words people use when they mention it.</summary>
public sealed record BibleStory(string Title, BibleReference Reference, string Words);

/// <summary>
/// The stories and passages preachers mention by name ("the prodigal son", "David and Goliath",
/// "the woman at the well"), so a mention finds the whole passage, not one verse.
/// </summary>
public static class BibleStories
{
    // Title | reference | other words people use for it
    private const string Data = """
        Creation|Genesis 1:1-31|in the beginning created heavens earth six days light darkness
        God rested on the seventh day|Genesis 2:1-3|sabbath rest seventh day
        Adam and Eve in the garden|Genesis 2:15-25|eden rib woman helper garden adam eve
        The fall of man|Genesis 3:1-24|serpent forbidden fruit tree knowledge good evil eve adam fall sin garden
        Cain and Abel|Genesis 4:1-16|brother keeper murder offering cain abel
        Noah and the flood|Genesis 6:9-22|ark flood noah animals rain
        The rainbow covenant|Genesis 9:8-17|rainbow covenant noah never flood again
        The tower of Babel|Genesis 11:1-9|tower babel languages confused scattered
        The call of Abraham|Genesis 12:1-9|abram leave your country father house bless you great nation
        God's covenant with Abraham, count the stars|Genesis 15:1-6|stars count descendants believed righteousness abram
        Hagar and Ishmael|Genesis 16:1-16|hagar ishmael sarai maid wilderness god who sees
        Sodom and Gomorrah|Genesis 19:1-29|sodom gomorrah lot wife pillar salt fire brimstone
        Abraham offers Isaac|Genesis 22:1-19|sacrifice isaac mount moriah ram thicket provide jehovah jireh
        Jacob and Esau, the birthright|Genesis 25:19-34|esau birthright stew red pottage twins jacob
        Jacob's ladder|Genesis 28:10-22|ladder angels ascending descending bethel dream stairway
        Jacob wrestles with God|Genesis 32:22-32|wrestled angel israel hip limp peniel blessing
        Joseph's coat and his brothers|Genesis 37:1-36|coat many colours colors dreams brothers sold pit joseph
        Joseph and Potiphar's wife|Genesis 39:1-23|potiphar wife fled garment prison joseph
        Joseph interprets Pharaoh's dream|Genesis 41:1-57|pharaoh dream seven fat lean cows famine joseph
        Joseph forgives his brothers|Genesis 45:1-15|joseph revealed brothers forgive you meant evil
        You meant it for evil, God meant it for good|Genesis 50:15-21|meant evil good joseph brothers
        Baby Moses in the basket|Exodus 2:1-10|moses basket bulrushes nile pharaoh daughter
        The burning bush|Exodus 3:1-15|burning bush moses holy ground i am that i am
        Let my people go, the plagues|Exodus 7:14-25|plagues let my people go pharaoh hardened heart blood
        The Passover|Exodus 12:1-28|passover lamb blood doorposts firstborn
        Crossing the Red Sea|Exodus 14:10-31|red sea parted moses rod dry ground egyptians drowned
        Manna from heaven|Exodus 16:1-36|manna quail bread from heaven wilderness
        Water from the rock|Exodus 17:1-7|water rock strike moses meribah
        Moses' hands held up, Aaron and Hur|Exodus 17:8-16|amalek hands held up aaron hur joshua
        The Ten Commandments|Exodus 20:1-17|ten commandments thou shalt not law sinai
        The golden calf|Exodus 32:1-35|golden calf idol aaron worship broke tablets
        The priestly blessing|Numbers 6:22-27|lord bless you keep you face shine upon you
        The twelve spies|Numbers 13:1-33|spies canaan grasshoppers giants caleb joshua milk honey
        The bronze serpent|Numbers 21:4-9|bronze brass serpent pole snakes bitten look live
        Balaam's donkey|Numbers 22:21-39|balaam donkey ass spoke angel
        Hear O Israel, love the Lord with all your heart|Deuteronomy 6:4-9|shema hear israel love lord all heart soul might
        Choose life|Deuteronomy 30:15-20|choose life death blessing curse
        Be strong and courageous|Joshua 1:1-9|strong courageous joshua meditate day night prosper
        Rahab and the spies|Joshua 2:1-24|rahab scarlet cord spies jericho
        The walls of Jericho|Joshua 6:1-27|jericho walls fell march around trumpets shout
        As for me and my house|Joshua 24:14-15|me and my house serve the lord choose this day
        Deborah and Barak|Judges 4:1-24|deborah barak jael sisera tent peg
        Gideon and the fleece|Judges 6:11-40|gideon fleece mighty man valour dew
        Gideon's three hundred men|Judges 7:1-25|gideon three hundred men lapped water trumpets jars torches
        Samson and Delilah|Judges 16:4-31|samson delilah hair strength philistines pillars
        Ruth and Naomi|Ruth 1:1-22|ruth naomi whither thou goest your people my people
        Ruth and Boaz|Ruth 2:1-23|boaz gleaning field ruth kinsman redeemer
        Hannah prays for a son|1 Samuel 1:1-28|hannah prayed son samuel barren eli
        The call of Samuel|1 Samuel 3:1-21|samuel speak lord your servant hears eli called
        Saul anointed king|1 Samuel 10:1-27|saul anointed king samuel
        To obey is better than sacrifice|1 Samuel 15:22-23|obey better than sacrifice rebellion witchcraft saul
        David anointed by Samuel|1 Samuel 16:1-13|david anointed jesse sons man looks outward appearance heart
        David and Goliath|1 Samuel 17:1-58|goliath giant sling stone david philistine
        David and Jonathan|1 Samuel 18:1-4|jonathan david friendship covenant
        David spares Saul's life|1 Samuel 24:1-22|david spared saul cave robe lord's anointed
        David encourages himself at Ziklag|1 Samuel 30:1-19|ziklag encouraged himself pursue overtake recover all
        David dances before the Lord|2 Samuel 6:12-23|david danced ark michal
        Mephibosheth at the king's table|2 Samuel 9:1-13|mephibosheth lame king's table kindness jonathan
        David and Bathsheba|2 Samuel 11:1-27|bathsheba uriah adultery david
        Nathan, you are the man|2 Samuel 12:1-15|nathan you are the man ewe lamb
        Solomon asks for wisdom|1 Kings 3:1-15|solomon wisdom dream understanding heart
        Solomon and the two mothers|1 Kings 3:16-28|two women baby sword divide wisdom solomon
        Elijah fed by ravens|1 Kings 17:1-7|elijah ravens brook cherith
        The widow of Zarephath|1 Kings 17:8-24|widow zarephath flour oil jar elijah son
        Elijah on Mount Carmel|1 Kings 18:20-40|carmel prophets baal fire fell altar elijah
        Elijah prays for rain|1 Kings 18:41-46|cloud like man's hand rain elijah seven times
        The still small voice|1 Kings 19:1-18|still small voice cave juniper broom tree elijah jezebel
        Naboth's vineyard|1 Kings 21:1-29|naboth vineyard ahab jezebel
        Elijah taken up to heaven|2 Kings 2:1-14|chariot fire whirlwind elisha double portion mantle
        The widow's oil|2 Kings 4:1-7|widow oil jars vessels debt elisha
        The Shunammite woman|2 Kings 4:8-37|shunammite woman son raised elisha
        Naaman healed of leprosy|2 Kings 5:1-19|naaman leprosy jordan seven times dip
        The axe head floats|2 Kings 6:1-7|axe head floated iron
        Open his eyes, chariots of fire|2 Kings 6:8-23|open his eyes servant horses chariots fire more with us
        Hezekiah's prayer and fifteen years|2 Kings 20:1-11|hezekiah sick fifteen years sundial
        If my people humble themselves|2 Chronicles 7:14|my people called by my name humble pray heal land
        Jehoshaphat, the battle is the Lord's|2 Chronicles 20:1-30|jehoshaphat battle not yours singers praise ambush
        Nehemiah rebuilds the wall|Nehemiah 2:1-20|nehemiah wall rebuild jerusalem
        The joy of the Lord is your strength|Nehemiah 8:1-12|joy of the lord strength ezra read law
        Esther, for such a time as this|Esther 4:1-17|esther such a time as this perish mordecai
        Job's suffering|Job 1:1-22|job satan lost everything naked came lord gave taken
        I know that my redeemer lives|Job 19:25-27|redeemer liveth lives job
        The Lord answers Job from the whirlwind|Job 38:1-41|whirlwind where were you foundations earth job
        Job restored|Job 42:10-17|job restored double prayed for friends
        Blessed is the man|Psalms 1:1-6|blessed man tree planted rivers counsel ungodly
        The Lord is my shepherd|Psalms 23:1-6|shepherd shall not want green pastures still waters valley shadow death table
        Lift up your heads, O gates|Psalms 24:1-10|earth is the lord's king of glory lift up heads gates
        God is our refuge and strength|Psalms 46:1-11|refuge strength present help trouble be still know
        Create in me a clean heart|Psalms 51:1-19|clean heart renew right spirit david repentance
        He who dwells in the secret place|Psalms 91:1-16|secret place most high shadow almighty pestilence thousand fall
        Enter his gates with thanksgiving|Psalms 100:1-5|joyful noise gates thanksgiving courts praise
        Bless the Lord, O my soul|Psalms 103:1-22|bless the lord my soul forget not benefits
        Your word is a lamp to my feet|Psalms 119:105|lamp feet light path word
        I lift up my eyes to the hills|Psalms 121:1-8|lift up eyes hills help cometh keeper slumber
        Unless the Lord builds the house|Psalms 127:1-5|lord build house labour vain children heritage
        How good and pleasant is unity|Psalms 133:1-3|brethren dwell together unity oil beard
        You have searched me and known me|Psalms 139:1-24|searched known fearfully wonderfully made
        Let everything that has breath praise the Lord|Psalms 150:1-6|breath praise trumpet cymbals
        Trust in the Lord with all your heart|Proverbs 3:5-6|trust lord all heart lean not understanding acknowledge paths
        The virtuous woman|Proverbs 31:10-31|virtuous woman wife noble character rubies
        A time for everything|Ecclesiastes 3:1-8|time season every purpose born die
        Remember your creator|Ecclesiastes 12:1-14|remember creator youth
        Isaiah's vision, here am I send me|Isaiah 6:1-8|here am i send me seraphim holy holy holy coal lips
        Unto us a child is born|Isaiah 9:6-7|child born son given wonderful counsellor prince peace
        Comfort my people|Isaiah 40:1-11|comfort ye voice crying wilderness prepare way
        They that wait upon the Lord|Isaiah 40:28-31|wait upon the lord renew strength wings eagles run not weary
        Fear not, I am with you|Isaiah 41:10|fear not dismayed strengthen help right hand
        When you pass through the waters|Isaiah 43:1-7|pass through waters fire not burned redeemed called by name
        The suffering servant|Isaiah 53:1-12|wounded transgressions stripes healed lamb slaughter despised rejected
        No weapon formed against you|Isaiah 54:17|no weapon formed prosper tongue condemn
        My thoughts are not your thoughts|Isaiah 55:8-11|thoughts ways higher heavens word not return void
        Arise, shine|Isaiah 60:1-3|arise shine light come glory risen
        The Spirit of the Lord is upon me|Isaiah 61:1-3|spirit lord upon me anointed good news poor beauty ashes
        The call of Jeremiah|Jeremiah 1:4-10|before i formed you womb knew you prophet nations
        The potter's house|Jeremiah 18:1-6|potter clay house vessel marred
        Plans to prosper you|Jeremiah 29:11-13|plans thoughts prosper hope future expected end
        Is anything too hard for the Lord|Jeremiah 32:17-27|anything too hard nothing too difficult
        Call to me and I will answer|Jeremiah 33:3|call unto me answer great mighty things
        His mercies are new every morning|Lamentations 3:22-24|mercies new every morning compassions fail great faithfulness
        The valley of dry bones|Ezekiel 37:1-14|dry bones valley prophesy breath live
        The fiery furnace|Daniel 3:1-30|shadrach meshach abednego furnace fire fourth man golden image
        The writing on the wall|Daniel 5:1-31|writing wall belshazzar mene tekel
        Daniel in the lions' den|Daniel 6:1-28|daniel lions den prayed three times window
        Hosea and Gomer|Hosea 1:1-11|hosea gomer prostitute unfaithful wife
        I will pour out my Spirit|Joel 2:28-32|pour out spirit all flesh sons daughters prophesy old men dreams
        I will restore the years|Joel 2:25-26|restore years locust eaten
        Jonah and the big fish|Jonah 1:1-17|jonah whale big fish swallowed nineveh storm
        Jonah preaches to Nineveh|Jonah 3:1-10|nineveh repented jonah preached
        What does the Lord require|Micah 6:8|do justly love mercy walk humbly
        Write the vision|Habakkuk 2:1-4|write vision plain tablets appointed time tarry wait
        Though the fig tree does not blossom|Habakkuk 3:17-19|fig tree blossom yet will rejoice hinds feet
        Not by might nor by power|Zechariah 4:6|not by might nor power but by my spirit
        Bring the tithes into the storehouse|Malachi 3:8-12|tithes offerings storehouse rob god windows heaven
        The birth of Jesus|Luke 2:1-20|birth jesus manger bethlehem shepherds angels christmas no room inn
        The wise men visit|Matthew 2:1-12|wise men magi star east gold frankincense myrrh herod
        The angel visits Mary|Luke 1:26-38|annunciation gabriel mary virgin conceive nothing impossible
        Boy Jesus in the temple|Luke 2:41-52|jesus twelve years temple father's business
        John the Baptist baptizes Jesus|Matthew 3:13-17|baptism jordan dove beloved son well pleased
        The temptation of Jesus|Matthew 4:1-11|temptation wilderness forty days devil stones bread
        Jesus calls the fishermen|Matthew 4:18-22|fishers of men follow me peter andrew nets
        The Beatitudes|Matthew 5:1-12|blessed are poor spirit mourn meek hunger thirst peacemakers sermon mount
        Salt and light|Matthew 5:13-16|salt earth light world city hill
        Love your enemies|Matthew 5:43-48|love enemies pray persecute
        The Lord's Prayer|Matthew 6:9-13|our father which art in heaven hallowed thy kingdom daily bread
        Do not worry, seek first the kingdom|Matthew 6:25-34|worry anxious birds air lilies seek first kingdom
        Ask, seek, knock|Matthew 7:7-11|ask given seek find knock opened
        The wise and foolish builders|Matthew 7:24-27|wise man built house rock foolish sand rain floods
        Jesus calms the storm|Mark 4:35-41|storm calmed peace be still boat wind waves asleep
        Come to me, all who are weary|Matthew 11:28-30|weary heavy laden rest yoke easy burden light
        The parable of the sower|Matthew 13:1-23|sower seed stony ground thorns good soil
        The mustard seed|Matthew 13:31-32|mustard seed smallest tree birds
        Feeding the five thousand|John 6:1-14|five thousand loaves fishes boy lunch fed baskets
        Jesus walks on water|Matthew 14:22-33|walked on water peter sink little faith
        Peter's confession, upon this rock|Matthew 16:13-20|who do you say i am christ son living god rock church keys
        The transfiguration|Matthew 17:1-13|transfigured mountain moses elijah shining
        Faith like a mustard seed moves mountains|Matthew 17:20|faith mustard seed move mountain
        Forgive seventy times seven|Matthew 18:21-35|forgive seventy times seven unforgiving servant
        Jesus and the little children|Mark 10:13-16|little children come unto me kingdom
        The rich young ruler|Mark 10:17-31|rich young ruler camel eye needle sell all
        Blind Bartimaeus|Mark 10:46-52|bartimaeus blind jericho son of david have mercy
        The triumphal entry|Matthew 21:1-11|palm sunday donkey hosanna entry jerusalem
        Jesus clears the temple|Matthew 21:12-17|money changers temple den thieves house prayer
        The greatest commandment|Matthew 22:34-40|greatest commandment love god neighbour yourself
        The ten virgins|Matthew 25:1-13|ten virgins lamps oil bridegroom wise foolish
        The parable of the talents|Matthew 25:14-30|talents servants buried faithful little ruler many
        The sheep and the goats|Matthew 25:31-46|sheep goats least of these hungry thirsty naked prison
        The Last Supper|Matthew 26:17-30|last supper bread wine body blood remembrance communion
        Jesus prays in Gethsemane|Matthew 26:36-46|gethsemane not my will cup watch pray spirit willing flesh weak
        Peter denies Jesus|Luke 22:54-62|peter denied cock crowed three times
        The crucifixion|Luke 23:26-49|crucified cross calvary golgotha thieves father forgive them
        The thief on the cross|Luke 23:39-43|thief cross paradise remember me
        The resurrection|Matthew 28:1-10|risen tomb empty stone rolled resurrection easter
        The Great Commission|Matthew 28:16-20|go into all the world make disciples baptizing great commission
        The paralysed man let down through the roof|Mark 2:1-12|paralytic roof friends lowered mat
        The woman with the issue of blood|Mark 5:25-34|woman issue blood touched hem garment twelve years faith made whole
        Jairus' daughter raised|Mark 5:21-43|jairus daughter talitha cumi sleeping
        The widow's mite|Mark 12:41-44|widow two mites coins gave all
        Zacchaeus|Luke 19:1-10|zacchaeus tax collector sycamore tree climbed
        The good Samaritan|Luke 10:25-37|good samaritan neighbour robbers priest levite inn
        Mary and Martha|Luke 10:38-42|mary martha sat feet distracted serving
        The rich fool|Luke 12:13-21|rich fool barns bigger soul required
        The lost sheep|Luke 15:1-7|lost sheep ninety nine shepherd
        The lost coin|Luke 15:8-10|lost coin silver woman lamp sweep
        The prodigal son|Luke 15:11-32|prodigal son father ran inheritance pigs far country elder brother lost son
        The rich man and Lazarus|Luke 16:19-31|rich man lazarus abraham bosom gulf
        The ten lepers|Luke 17:11-19|ten lepers one returned thanks samaritan
        The persistent widow|Luke 18:1-8|persistent widow unjust judge pray not faint
        The Pharisee and the tax collector|Luke 18:9-14|pharisee publican tax collector be merciful sinner
        The road to Emmaus|Luke 24:13-35|emmaus road hearts burn broke bread
        In the beginning was the Word|John 1:1-14|word was god became flesh light darkness
        Water into wine|John 2:1-11|water wine wedding cana first miracle
        Nicodemus, you must be born again|John 3:1-21|nicodemus born again night god so loved world
        The woman at the well|John 4:1-42|samaritan woman well living water worship spirit truth
        The pool of Bethesda|John 5:1-15|bethesda pool thirty eight years take up your bed
        I am the bread of life|John 6:35-40|bread of life hunger never
        The woman caught in adultery|John 8:1-11|adultery stone first cast sin no more
        The truth will set you free|John 8:31-36|truth make you free free indeed
        Jesus heals the man born blind|John 9:1-41|born blind clay spittle siloam once blind now see
        I am the good shepherd|John 10:1-18|good shepherd sheep voice thief abundant life
        Lazarus raised from the dead|John 11:1-44|lazarus raised dead resurrection life jesus wept come forth
        Jesus washes the disciples' feet|John 13:1-17|washed feet towel basin servant
        I am the way, the truth and the life|John 14:1-14|way truth life father's house many mansions troubled
        I am the vine|John 15:1-17|vine branches abide fruit
        Doubting Thomas|John 20:24-29|thomas doubted wounds side believe
        Do you love me, feed my sheep|John 21:15-19|lovest thou me feed my sheep lambs peter
        The ascension|Acts 1:1-11|ascension cloud taken up power witnesses
        The day of Pentecost|Acts 2:1-13|pentecost holy spirit tongues fire mighty wind upper room
        Peter's sermon at Pentecost|Acts 2:14-41|repent baptized three thousand
        The early church|Acts 2:42-47|apostles doctrine fellowship breaking bread prayers all things common
        The lame man at the Beautiful Gate|Acts 3:1-10|lame man beautiful gate silver gold such as i have rise walk
        Ananias and Sapphira|Acts 5:1-11|ananias sapphira lied holy spirit
        The stoning of Stephen|Acts 7:54-60|stephen stoned saw heaven opened lay not this sin
        Philip and the Ethiopian|Acts 8:26-40|philip ethiopian eunuch chariot isaiah baptized
        The conversion of Saul|Acts 9:1-19|saul damascus road light blinded conversion ananias
        Peter's vision and Cornelius|Acts 10:1-48|cornelius sheet vision unclean gentiles
        Peter freed from prison|Acts 12:1-19|peter prison angel chains rhoda
        Paul and Silas in prison|Acts 16:16-40|paul silas prison midnight praises earthquake jailer
        Paul in Athens, the unknown god|Acts 17:16-34|athens unknown god areopagus mars hill
        Paul shipwrecked|Acts 27:13-44|shipwreck storm malta
        Not ashamed of the gospel|Romans 1:16-17|not ashamed gospel power salvation just live by faith
        All have sinned|Romans 3:21-26|all have sinned fall short glory
        Justified by faith, peace with God|Romans 5:1-11|justified faith peace tribulation patience hope
        The wages of sin is death|Romans 6:15-23|wages sin death gift eternal life
        The struggle with sin|Romans 7:14-25|good i would do not evil wretched man
        No condemnation|Romans 8:1-17|no condemnation spirit life law sin death
        All things work together for good|Romans 8:28-30|all things work together good love god called purpose
        More than conquerors|Romans 8:31-39|more than conquerors separate love god nothing
        Confess with your mouth|Romans 10:9-17|confess mouth believe heart saved faith comes hearing
        A living sacrifice|Romans 12:1-2|living sacrifice renewing mind conformed transformed
        The body of Christ|1 Corinthians 12:12-27|body many members one body hand foot eye
        Love is patient, love is kind|1 Corinthians 13:1-13|love patient kind charity faith hope love greatest
        The resurrection chapter|1 Corinthians 15:50-58|twinkling eye trumpet death sting victory
        Treasure in jars of clay|2 Corinthians 4:7-18|treasure earthen vessels jars clay troubled not distressed
        A new creation|2 Corinthians 5:17-21|new creature creation old things passed away ambassadors
        My grace is sufficient|2 Corinthians 12:7-10|grace sufficient thorn flesh weakness strength
        The fruit of the Spirit|Galatians 5:22-23|fruit spirit love joy peace patience kindness goodness gentleness self control
        Saved by grace through faith|Ephesians 2:1-10|grace saved through faith not works gift workmanship
        The armour of God|Ephesians 6:10-18|armour armor god belt truth breastplate shield faith helmet sword spirit
        The mind of Christ|Philippians 2:5-11|mind christ humbled himself every knee bow tongue confess
        Press toward the mark|Philippians 3:12-14|press toward mark forgetting things behind prize high calling
        Do not be anxious, the peace of God|Philippians 4:4-9|rejoice always anxious nothing prayer peace passes understanding think on these things
        I can do all things through Christ|Philippians 4:10-13|all things through christ strengthens content
        My God shall supply all your needs|Philippians 4:19|supply all your need riches glory
        The coming of the Lord|1 Thessalonians 4:13-18|caught up clouds dead in christ rise first trumpet rapture
        Pray without ceasing|1 Thessalonians 5:16-18|rejoice evermore pray without ceasing give thanks everything
        Fight the good fight|2 Timothy 4:6-8|fought good fight finished race kept faith crown
        All scripture is inspired|2 Timothy 3:14-17|all scripture inspired god profitable doctrine reproof
        The word of God is living and active|Hebrews 4:12-16|word quick powerful sharper two edged sword throne grace boldly
        The heroes of faith|Hebrews 11:1-40|faith substance hoped evidence not seen hall faith abel enoch noah
        The great cloud of witnesses|Hebrews 12:1-3|cloud witnesses run race patience looking unto jesus
        Count it all joy|James 1:2-8|count it all joy trials testing patience wisdom ask
        Faith without works is dead|James 2:14-26|faith without works dead
        Taming the tongue|James 3:1-12|tongue fire rudder bridle
        The prayer of faith|James 5:13-18|prayer faith sick anoint oil elders righteous man availeth elijah
        Cast your cares on him|1 Peter 5:6-11|cast cares anxiety humble devil roaring lion
        If we confess our sins|1 John 1:5-10|confess sins faithful just forgive cleanse walk light
        God is love|1 John 4:7-21|god is love perfect love casts out fear
        Behold, I stand at the door and knock|Revelation 3:14-22|stand door knock laodicea lukewarm hot cold
        The throne room of heaven|Revelation 4:1-11|throne twenty four elders holy holy holy crowns
        A new heaven and a new earth|Revelation 21:1-7|new heaven new earth wipe tears no more death new jerusalem
        """;

    private static readonly Lazy<IReadOnlyList<BibleStory>> _all = new(Load);

    public static IReadOnlyList<BibleStory> All => _all.Value;

    private static IReadOnlyList<BibleStory> Load()
    {
        var list = new List<BibleStory>();
        foreach (var raw in Data.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var parts = line.Split('|');
            if (parts.Length != 3) throw new InvalidDataException("Bad story line: " + line);
            var r = ReferenceParser.Parse(parts[1]);
            if (!r.Ok) throw new InvalidDataException($"Bad story reference: {parts[1]} ({r.Error})");
            list.Add(new BibleStory(parts[0], r.Reference!, parts[2]));
        }
        return list;
    }
}
