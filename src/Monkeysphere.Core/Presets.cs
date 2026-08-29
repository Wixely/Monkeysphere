namespace Monkeysphere.Core;

public sealed record PresetField(
    string CanonicalKey,
    string Name,
    string TypeId,
    bool IsRequired = false,
    IReadOnlyList<string>? ChoiceOptions = null);

public sealed record RecordTypePreset(
    string Key,
    int Version,
    string Name,
    string Category,
    string Description,
    IReadOnlyList<string> Examples,
    IReadOnlyList<PresetField> Fields);

public sealed record RelationshipTypePreset(
    string Key,
    int Version,
    string Name,
    string InverseName,
    IReadOnlyList<string> RequiredPresetKeys,
    IReadOnlyList<string> AnyPresetKeys);

public sealed record StarterPack(
    string Key,
    string Name,
    string Description,
    IReadOnlyList<string> ExampleItems,
    IReadOnlyList<string> PresetKeys);

public sealed record SetupStatus(bool IsComplete, string? StarterPackKey, DateTimeOffset? CompletedAtUtc);

public sealed record PresetFieldInstallation(Guid Id, PresetField Definition, string ConfigurationJson);

public sealed record RecordTypePresetInstallation(
    Guid Id,
    RecordTypePreset Preset,
    IReadOnlyList<PresetFieldInstallation> Fields);

public sealed record RelationshipTypePresetInstallation(Guid Id, RelationshipTypePreset Preset);

public sealed record PresetInstallation(
    string? StarterPackKey,
    IReadOnlyList<RecordTypePresetInstallation> RecordTypes,
    IReadOnlyList<RelationshipTypePresetInstallation> RelationshipTypes,
    DateTimeOffset InstalledAtUtc);

public interface IPresetStore
{
    Task<SetupStatus> GetSetupStatusAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlySet<string>> ListInstalledPresetKeysAsync(CancellationToken cancellationToken = default);
    Task InstallAsync(PresetInstallation installation, CancellationToken cancellationToken = default);
}

public interface IPresetService
{
    IReadOnlyList<RecordTypePreset> RecordTypes { get; }
    IReadOnlyList<StarterPack> StarterPacks { get; }
    Task<SetupStatus> GetSetupStatusAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlySet<string>> ListInstalledPresetKeysAsync(CancellationToken cancellationToken = default);
    Task InstallPresetAsync(string presetKey, CancellationToken cancellationToken = default);
    Task CompleteSetupAsync(string starterPackKey, IReadOnlyCollection<string> selectedPresetKeys, CancellationToken cancellationToken = default);
}

public sealed class PresetService(IPresetStore store, TimeProvider timeProvider) : IPresetService
{
    public IReadOnlyList<RecordTypePreset> RecordTypes => PresetCatalog.RecordTypes;
    public IReadOnlyList<StarterPack> StarterPacks => PresetCatalog.StarterPacks;

    public Task<SetupStatus> GetSetupStatusAsync(CancellationToken cancellationToken = default) =>
        store.GetSetupStatusAsync(cancellationToken);

    public Task<IReadOnlySet<string>> ListInstalledPresetKeysAsync(CancellationToken cancellationToken = default) =>
        store.ListInstalledPresetKeysAsync(cancellationToken);

    public async Task InstallPresetAsync(string presetKey, CancellationToken cancellationToken = default)
    {
        RecordTypePreset preset = FindPreset(presetKey);
        IReadOnlySet<string> installed = await store.ListInstalledPresetKeysAsync(cancellationToken).ConfigureAwait(false);
        if (installed.Contains(preset.Key))
        {
            throw new DomainValidationException($"The {preset.Name} preset is already installed.");
        }

        await store.InstallAsync(CreateInstallation(null, [preset]), cancellationToken).ConfigureAwait(false);
    }

    public async Task CompleteSetupAsync(
        string starterPackKey,
        IReadOnlyCollection<string> selectedPresetKeys,
        CancellationToken cancellationToken = default)
    {
        SetupStatus status = await store.GetSetupStatusAsync(cancellationToken).ConfigureAwait(false);
        if (status.IsComplete)
        {
            throw new DomainValidationException("First-run setup has already been completed.");
        }

        StarterPack pack = PresetCatalog.StarterPacks.FirstOrDefault(item =>
            string.Equals(item.Key, starterPackKey, StringComparison.Ordinal))
            ?? throw new DomainValidationException("Starter pack was not found.");
        HashSet<string> selected = selectedPresetKeys.ToHashSet(StringComparer.Ordinal);
        if (!selected.IsSubsetOf(pack.PresetKeys))
        {
            throw new DomainValidationException("The starter selection contains a preset outside that pack.");
        }

        RecordTypePreset[] presets = pack.PresetKeys
            .Where(selected.Contains)
            .Select(FindPreset)
            .ToArray();
        await store.InstallAsync(CreateInstallation(pack.Key, presets), cancellationToken).ConfigureAwait(false);
    }

    private PresetInstallation CreateInstallation(string? starterPackKey, IReadOnlyList<RecordTypePreset> presets)
    {
        HashSet<string> keys = presets.Select(item => item.Key).ToHashSet(StringComparer.Ordinal);
        RecordTypePresetInstallation[] types = presets.Select(preset => new RecordTypePresetInstallation(
            Guid.CreateVersion7(),
            preset,
            preset.Fields.Select(field => new PresetFieldInstallation(
                Guid.CreateVersion7(),
                field,
                FieldTypes.NormalizeConfiguration(field.TypeId, field.ChoiceOptions))).ToArray())).ToArray();
        RelationshipTypePresetInstallation[] relationships = PresetCatalog.RelationshipTypes
            .Where(preset => preset.RequiredPresetKeys.All(keys.Contains) &&
                (preset.AnyPresetKeys.Count == 0 || preset.AnyPresetKeys.Any(keys.Contains)))
            .Select(preset => new RelationshipTypePresetInstallation(Guid.CreateVersion7(), preset))
            .ToArray();
        return new(starterPackKey, types, relationships, timeProvider.GetUtcNow());
    }

    private static RecordTypePreset FindPreset(string key) =>
        PresetCatalog.RecordTypes.FirstOrDefault(item => string.Equals(item.Key, key, StringComparison.Ordinal))
        ?? throw new DomainValidationException("Record-type preset was not found.");
}

public static class PresetCatalog
{
    private const string Person = "monkeysphere.person";
    private const string Cat = "monkeysphere.cat";
    private const string Dog = "monkeysphere.dog";
    private const string SmallPet = "monkeysphere.small-pet";
    private const string Vehicle = "monkeysphere.vehicle";
    private const string VideoGame = "monkeysphere.video-game";
    private const string BoardGame = "monkeysphere.board-game";
    private const string Book = "monkeysphere.book";
    private const string FilmSeries = "monkeysphere.film-series";
    private const string Plant = "monkeysphere.plant";
    private const string Home = "monkeysphere.home";
    private const string Workplace = "monkeysphere.workplace";
    private const string FavouritePlace = "monkeysphere.favourite-place";
    private const string Trip = "monkeysphere.trip";
    private const string Event = "monkeysphere.event";

    public static IReadOnlyList<RecordTypePreset> RecordTypes { get; } =
    [
        Type(Person, "Person", "People", "Family, friends, colleagues, and everyone worth remembering.", ["a sibling", "a close friend", "a former colleague"],
            Text(Person, "pronouns", "Pronouns"), Temporal(Person, "birthday", "Birthday"), Text(Person, "email", "Email"),
            Field(Person, "phone", "Phone", FieldTypes.PhoneNumber), Field(Person, "website", "Website", FieldTypes.WebLink),
            Tags(Person, "likes", "Likes"), Tags(Person, "dislikes", "Dislikes"), Notes(Person)),
        Type(Cat, "Cat", "Companions", "Cats and the details that make each one distinctive.", ["the family cat", "a foster cat"],
            Text(Cat, "breed", "Breed"), Temporal(Cat, "birthday", "Birthday"), Text(Cat, "colour", "Colour"),
            Text(Cat, "microchip", "Microchip number"), Tags(Cat, "likes", "Likes"), Tags(Cat, "dislikes", "Dislikes"), Notes(Cat)),
        Type(Dog, "Dog", "Companions", "Dogs, their history, preferences, and care notes.", ["your dog", "a dog you regularly look after"],
            Text(Dog, "breed", "Breed"), Temporal(Dog, "birthday", "Birthday"), Text(Dog, "colour", "Colour"),
            Text(Dog, "microchip", "Microchip number"), Tags(Dog, "likes", "Likes"), Tags(Dog, "dislikes", "Dislikes"), Notes(Dog)),
        Type(SmallPet, "Small Pet", "Companions", "Hamsters, rabbits, guinea pigs, and other small companions.", ["a hamster", "a rabbit", "a guinea pig"],
            Text(SmallPet, "species", "Species", true), Text(SmallPet, "breed", "Breed"), Temporal(SmallPet, "birthday", "Birthday"),
            Text(SmallPet, "colour", "Colour"), Tags(SmallPet, "likes", "Likes"), Tags(SmallPet, "dislikes", "Dislikes"), Notes(SmallPet)),
        Type(Vehicle, "Vehicle", "Possessions", "Cars, motorcycles, vans, campers, boats, and other vehicles.", ["the family car", "a project motorcycle", "a camper van"],
            Choice(Vehicle, "kind", "Vehicle type", ["Car", "Motorcycle", "Van", "Camper", "Boat", "Other"]),
            Text(Vehicle, "make", "Make"), Text(Vehicle, "model", "Model"), Temporal(Vehicle, "year", "Year"),
            Text(Vehicle, "registration", "Registration"), Text(Vehicle, "colour", "Colour"), Text(Vehicle, "vin", "VIN"), Notes(Vehicle)),
        Type(VideoGame, "Video Game", "Games and media", "Games you own, play, finish, revisit, or recommend.", ["a current favourite", "a childhood classic", "your backlog"],
            Tags(VideoGame, "platforms", "Platforms"), Temporal(VideoGame, "release", "Release date"),
            Choice(VideoGame, "status", "Status", ["Backlog", "Playing", "Completed", "Abandoned"]),
            Number(VideoGame, "rating", "Rating"), Text(VideoGame, "developer", "Developer"), Tags(VideoGame, "genres", "Genres"), Notes(VideoGame)),
        Type(BoardGame, "Board Game", "Games and media", "Tabletop games, play status, and the groups who enjoy them.", ["a party game", "a campaign game", "a family favourite"],
            Text(BoardGame, "publisher", "Publisher"), Temporal(BoardGame, "release", "Release date"), Number(BoardGame, "minimum-players", "Minimum players"),
            Number(BoardGame, "maximum-players", "Maximum players"), Number(BoardGame, "rating", "Rating"), Tags(BoardGame, "genres", "Genres"), Notes(BoardGame)),
        Type(Book, "Book", "Games and media", "Books you are reading, have read, own, or want to remember.", ["a favourite novel", "a reference book", "your reading list"],
            Text(Book, "author", "Author"), Text(Book, "isbn", "ISBN"), Temporal(Book, "published", "Published"),
            Choice(Book, "status", "Status", ["Want to read", "Reading", "Read", "Paused", "Abandoned"]),
            Number(Book, "rating", "Rating"), Tags(Book, "genres", "Genres"), Notes(Book)),
        Type(FilmSeries, "Film or Series", "Games and media", "Films, television series, and miniseries worth tracking.", ["a comfort film", "a series in progress", "something to watch"],
            Choice(FilmSeries, "format", "Format", ["Film", "TV series", "Miniseries", "Other"]), Temporal(FilmSeries, "release", "Release date"),
            Choice(FilmSeries, "status", "Status", ["Watchlist", "Watching", "Watched", "Paused", "Abandoned"]),
            Number(FilmSeries, "rating", "Rating"), Tags(FilmSeries, "creators", "Creators"), Tags(FilmSeries, "genres", "Genres"), Notes(FilmSeries)),
        Type(Plant, "Plant", "Living things", "Houseplants, garden plants, and their care history.", ["a houseplant", "a fruit tree", "a favourite garden plant"],
            Text(Plant, "species", "Species"), Temporal(Plant, "acquired", "Acquired"), Text(Plant, "location", "Location"),
            Temporal(Plant, "last-repotted", "Last repotted"), Field(Plant, "care", "Care notes", FieldTypes.MultilineText), Notes(Plant)),
        Type(Home, "Home", "Places", "Current and former homes with location at the appropriate accuracy.", ["your current home", "a childhood home", "a holiday home"],
            Location(Home), Radius(Home), Temporal(Home, "since", "Since"), Temporal(Home, "until", "Until"), Notes(Home)),
        Type(Workplace, "Workplace", "Places", "Offices, studios, workshops, and other places of work.", ["an office", "a studio", "a former workplace"],
            Text(Workplace, "organisation", "Organisation"), Location(Workplace), Radius(Workplace), Field(Workplace, "website", "Website", FieldTypes.WebLink), Notes(Workplace)),
        Type(FavouritePlace, "Favourite Place", "Places", "Places you return to or want to recommend.", ["a favourite café", "a quiet park", "a much-loved venue"],
            Choice(FavouritePlace, "kind", "Place type", ["Restaurant or café", "Park", "Venue", "Shop", "Viewpoint", "Other"]),
            Location(FavouritePlace), Radius(FavouritePlace), Field(FavouritePlace, "website", "Website", FieldTypes.WebLink), Tags(FavouritePlace, "likes", "What you like"), Notes(FavouritePlace)),
        Type(Trip, "Trip or Journey", "Experiences", "Journeys, holidays, and visits that connect people and places.", ["a weekend away", "a family holiday", "a memorable road trip"],
            Temporal(Trip, "start", "Start"), Temporal(Trip, "end", "End"), Tags(Trip, "destinations", "Destinations"),
            Tags(Trip, "highlights", "Highlights"), Notes(Trip)),
        Type(Event, "Event", "Experiences", "Appointments, celebrations, performances, and memorable occasions.", ["a birthday party", "a concert", "an annual gathering"],
            Choice(Event, "kind", "Event type", ["Celebration", "Performance", "Appointment", "Gathering", "Milestone", "Other"]),
            Temporal(Event, "start", "Start"), Temporal(Event, "end", "End"), Location(Event), Radius(Event), Field(Event, "website", "Website", FieldTypes.WebLink), Notes(Event)),
    ];

    public static IReadOnlyList<RelationshipTypePreset> RelationshipTypes { get; } =
    [
        Relation("monkeysphere.relationship.owns", "owns", "owned by", [Person], [Vehicle, VideoGame, BoardGame, Book, FilmSeries]),
        Relation("monkeysphere.relationship.cares-for", "cares for", "cared for by", [Person], [Cat, Dog, SmallPet, Plant]),
        Relation("monkeysphere.relationship.played", "played", "played by", [Person], [VideoGame, BoardGame]),
        Relation("monkeysphere.relationship.completed", "completed", "completed by", [Person, VideoGame], []),
        Relation("monkeysphere.relationship.read", "read", "read by", [Person, Book], []),
        Relation("monkeysphere.relationship.watched", "watched", "watched by", [Person, FilmSeries], []),
        Relation("monkeysphere.relationship.lives-at", "lives at", "home of", [Person, Home], []),
        Relation("monkeysphere.relationship.works-at", "works at", "workplace of", [Person, Workplace], []),
        Relation("monkeysphere.relationship.visited", "visited", "visited by", [Person, FavouritePlace], []),
        Relation("monkeysphere.relationship.attended", "attended", "attended by", [Person, Event], []),
    ];

    public static IReadOnlyList<StarterPack> StarterPacks { get; } =
    [
        new("blank", "Blank slate", "Start with no premade record types and build exactly what you need.",
            ["a completely custom collection"], []),
        new("people", "Just people", "A focused setup for the people in your life.",
            ["family", "friends", "colleagues"], [Person]),
        new("everyday", "People and everyday life", "People, companions, possessions, hobbies, and entertainment.",
            ["your cat", "the family car", "a favourite video game", "a book you are reading"],
            [Person, Cat, Dog, SmallPet, Vehicle, VideoGame, BoardGame, Book, FilmSeries, Plant]),
        new("full", "Your whole world", "The broad starter setup, including places, journeys, and events.",
            ["your home", "a favourite café", "a memorable trip", "an annual gathering"],
            [Person, Cat, Dog, SmallPet, Vehicle, VideoGame, BoardGame, Book, FilmSeries, Plant, Home, Workplace, FavouritePlace, Trip, Event]),
    ];

    private static RecordTypePreset Type(string key, string name, string category, string description, IReadOnlyList<string> examples, params PresetField[] fields) =>
        new(key, 1, name, category, description, examples, fields);
    private static PresetField Field(string preset, string key, string name, string type, bool required = false, IReadOnlyList<string>? options = null) =>
        new($"{preset}.{key}", name, type, required, options);
    private static PresetField Text(string preset, string key, string name, bool required = false) => Field(preset, key, name, FieldTypes.Text, required);
    private static PresetField Number(string preset, string key, string name) => Field(preset, key, name, FieldTypes.Number);
    private static PresetField Temporal(string preset, string key, string name) => Field(preset, key, name, FieldTypes.Temporal);
    private static PresetField Tags(string preset, string key, string name) => Field(preset, key, name, FieldTypes.Tags);
    private static PresetField Choice(string preset, string key, string name, IReadOnlyList<string> options) => Field(preset, key, name, FieldTypes.Choice, options: options);
    private static PresetField Notes(string preset) => Field(preset, "notes", "Notes", FieldTypes.MultilineText);
    private static PresetField Location(string preset) => Field(preset, "location", "Location", FieldTypes.MultilineText);
    private static PresetField Radius(string preset) => Field(preset, "approximation-radius-km", "Approximation radius (km)", FieldTypes.Number);
    private static RelationshipTypePreset Relation(string key, string name, string inverse, IReadOnlyList<string> required, IReadOnlyList<string> any) =>
        new(key, 1, name, inverse, required, any);
}
