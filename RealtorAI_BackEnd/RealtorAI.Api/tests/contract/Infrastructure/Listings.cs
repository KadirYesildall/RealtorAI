using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using ImageMagick;

namespace RealtorAI.Tests.Contract.Infrastructure;

/// <summary>
/// Brokerages and memberships arranged directly in the database, for listing tests that need a
/// particular relationship between two agents.
///
/// <para>
/// Directly, not through <c>POST /api/brokerages/memberships</c>: declaring is audited, and an
/// audited membership can never be removed, so arranging relationships through the API would
/// leave permanent applicants behind every run. These rows carry no audit references. The
/// declaration flow itself is covered by <c>BrokerageMembershipTests</c>.
/// </para>
/// </summary>
internal static class TestBrokerages
{
    /// <summary>A new brokerage whose approved broker admin is <paramref name="adminId"/>.</summary>
    internal static async Task<Guid> CreateAsync(Guid adminId, string? targetMlsId = null)
    {
        var id = Guid.CreateVersion7();

        await TestDatabase.ExecuteAsync(
            "insert into public.brokerages (id, name, license_number, state) values ($1, $2, $3, 'CA')",
            id,
            "Listing Test Realty " + id.ToString("N")[..6],
            "CA-DRE-" + id.ToString("N")[..8]);

        await TestDatabase.ExecuteAsync(
            "insert into public.brokerage_settings (brokerage_id, target_mls_id) values ($1, $2)",
            id,
            targetMlsId);

        await AddMemberAsync(adminId, id, "approved", "broker_admin");

        return id;
    }

    /// <summary>Adds <paramref name="agentId"/> to the brokerage with the given status and role.</summary>
    internal static async Task AddMemberAsync(Guid agentId, Guid brokerageId, string status, string role = "agent")
    {
        var decided = status != "pending";

        await TestDatabase.ExecuteAsync(
            """
            insert into public.brokerage_memberships
                (agent_id, brokerage_id, status, role, decided_by, decided_at)
            values ($1, $2, $3::membership_status, $4::membership_role,
                    case when $5 then $1 end,
                    case when $5 then timezone('utc', now()) end)
            """,
            agentId,
            brokerageId,
            status,
            role,
            decided);
    }
}

/// <summary>Listing requests and calls shared by the US1 suites.</summary>
internal static class TestListings
{
    internal const string Listings = "/api/listings";

    internal static string Listing(Guid listingId) => $"/api/listings/{listingId}";

    internal static string Fields(Guid listingId) => $"/api/listings/{listingId}/fields";

    internal static string Confirm(Guid listingId, Guid fieldId) => $"/api/listings/{listingId}/fields/{fieldId}/confirm";

    internal static string Photos(Guid listingId) => $"/api/listings/{listingId}/photos";

    /// <summary>The six basic details of Story 1, all valid.</summary>
    internal static JsonObject Basics() => new()
    {
        ["addressLine1"] = "1420 Crestline Drive",
        ["city"] = "Irvine",
        ["state"] = "CA",
        ["postalCode"] = "92618",
        ["propertyType"] = "SingleFamilyResidence",
        ["listPrice"] = 1485000,
        ["bedrooms"] = 4,
        ["bathrooms"] = 3.5,
        ["agentNotes"] = "Seller prefers weekend showings.",
    };

    /// <summary>Creates a listing as <paramref name="agentId"/> and returns the response body.</summary>
    internal static async Task<JsonObject> CreateAsync(ApiFactory factory, Guid agentId, JsonObject? body = null)
    {
        using var client = factory.CreateClientFor(agentId);

        var response = await client.PostAsJsonAsync(Listings, body ?? Basics());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<JsonObject>())!;
    }

    internal static Guid IdOf(JsonObject listing) => listing["id"]!.GetValue<Guid>();

    /// <summary>A PATCH or PUT carrying <c>If-Match</c>.</summary>
    internal static HttpRequestMessage Conditional(HttpMethod method, string path, JsonObject body, int? ifMatch)
    {
        var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) };

        if (ifMatch is { } version)
        {
            request.Headers.TryAddWithoutValidation("If-Match", $"\"{version}\"");
        }

        return request;
    }

    /// <summary>A multipart body of files, each under the form name <c>files</c>.</summary>
    internal static MultipartFormDataContent Files(params (string FileName, byte[] Content, string ContentType)[] files)
    {
        var form = new MultipartFormDataContent();

        foreach (var (fileName, content, contentType) in files)
        {
            var part = new ByteArrayContent(content);
            part.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            form.Add(part, "files", fileName);
        }

        return form;
    }
}

/// <summary>
/// Photos generated per test, so every upload has distinct bytes — two tests uploading the same
/// fixture to one listing would trip duplicate detection instead of the behaviour under test.
/// </summary>
internal static class TestImages
{
    internal static byte[] Jpeg(uint width = 1600, uint height = 1200) => Create(MagickFormat.Jpeg, width, height);

    internal static byte[] Png(uint width = 1200, uint height = 900) => Create(MagickFormat.Png, width, height);

    internal static byte[] WebP(uint width = 1400, uint height = 1000) => Create(MagickFormat.WebP, width, height);

    /// <summary>
    /// A JPEG of one colour with light noise. Two of the same colour are distinct files that
    /// look alike — the near-duplicate case.
    /// </summary>
    internal static byte[] JpegOfColour(byte red, byte green, byte blue, uint width = 1200, uint height = 900)
    {
        using var image = new MagickImage(new MagickColor(red, green, blue), width, height);
        image.AddNoise(NoiseType.Gaussian, 0.2);
        image.Format = MagickFormat.Jpeg;

        return image.ToByteArray();
    }

    /// <summary>A JPEG carrying GPS coordinates, the metadata the normalized copy must lose.</summary>
    internal static byte[] JpegWithGps(uint width = 2400, uint height = 1600)
    {
        using var image = Canvas(width, height);

        var exif = new ExifProfile();
        exif.SetValue(ExifTag.GPSLatitudeRef, "N");
        exif.SetValue(ExifTag.GPSLatitude, [new Rational(33, 1), new Rational(40, 1), new Rational(1524, 100)]);
        exif.SetValue(ExifTag.GPSLongitudeRef, "W");
        exif.SetValue(ExifTag.GPSLongitude, [new Rational(117, 1), new Rational(49, 1), new Rational(3672, 100)]);
        image.SetProfile(exif);
        image.Format = MagickFormat.Jpeg;

        return image.ToByteArray();
    }

    private static byte[] Create(MagickFormat format, uint width, uint height)
    {
        using var image = Canvas(width, height);
        image.Format = format;

        return image.ToByteArray();
    }

    /// <summary>A solid image in a random colour with random noise, so no two are identical.</summary>
    private static MagickImage Canvas(uint width, uint height)
    {
        var color = new MagickColor(
            (byte)Random.Shared.Next(256),
            (byte)Random.Shared.Next(256),
            (byte)Random.Shared.Next(256));

        var image = new MagickImage(color, width, height);
        image.AddNoise(NoiseType.Gaussian);

        return image;
    }
}
