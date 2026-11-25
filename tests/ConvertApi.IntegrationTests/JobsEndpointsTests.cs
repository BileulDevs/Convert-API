using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace ConvertApi.IntegrationTests;

public sealed class JobsEndpointsTests(ConvertApiFactory factory) : IClassFixture<ConvertApiFactory>
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task PostJob_WithAPng_Returns202AndStoresTheFile()
    {
        using var client = factory.CreateClient();
        var png = Png(size: 200);
        var storedBefore = factory.StoredFiles().Length;

        using var response = await client.PostAsync(JobsUri, Form(png, "photo.png", "jpg"), Ct);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var job = await ReadJobAsync(response);
        Assert.Equal($"/v1/jobs/{job.GetProperty("id").GetGuid()}", response.Headers.Location?.OriginalString);
        Assert.Equal("Queued", job.GetProperty("status").GetString());
        Assert.Equal("png", job.GetProperty("sourceFormat").GetString());
        Assert.Equal("jpg", job.GetProperty("targetFormat").GetString());
        var stored = factory.StoredFiles();
        Assert.Equal(storedBefore + 1, stored.Length);
        Assert.Contains(stored, path => File.ReadAllBytes(path).SequenceEqual(png));
    }

    [Fact]
    public async Task GetJob_ReturnsTheJobAtItsTrackingUrl()
    {
        using var client = factory.CreateClient();
        using var created = await client.PostAsync(JobsUri, Form(Png(size: 50), "photo.png", ".JPG"), Ct);

        using var response = await client.GetAsync(created.Headers.Location, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var job = await ReadJobAsync(response);
        Assert.Equal((await ReadJobAsync(created)).GetProperty("id").GetGuid(), job.GetProperty("id").GetGuid());
        Assert.Equal("jpg", job.GetProperty("targetFormat").GetString());
    }

    [Fact]
    public async Task PostJob_TrustsTheContent_NotTheFileNameOrContentType()
    {
        using var client = factory.CreateClient();

        using var response = await client.PostAsync(JobsUri, Form(Png(size: 50), "song.mp3", "webp", "audio/mpeg"), Ct);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("png", (await ReadJobAsync(response)).GetProperty("sourceFormat").GetString());
    }

    [Fact]
    public async Task PostJob_WithUnrecognizedContent_Returns415AndKeepsNothing()
    {
        using var client = factory.CreateClient();
        var storedBefore = factory.StoredFiles().Length;

        using var response = await client.PostAsync(JobsUri, Form(Encoding.UTF8.GetBytes("not an image"), "photo.png", "jpg"), Ct);

        await AssertProblemAsync(response, HttpStatusCode.UnsupportedMediaType);
        Assert.Equal(storedBefore, factory.StoredFiles().Length);
    }

    [Theory]
    [InlineData("mp3")]
    [InlineData("tiff")]
    [InlineData("../x")]
    public async Task PostJob_WithAnUnsupportedConversion_Returns422AndKeepsNothing(string target)
    {
        using var client = factory.CreateClient();
        var storedBefore = factory.StoredFiles().Length;

        using var response = await client.PostAsync(JobsUri, Form(Png(size: 50), "photo.png", target), Ct);

        var problem = await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity);
        Assert.Contains("bmp, gif, jpg, png, webp", problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.Equal(storedBefore, factory.StoredFiles().Length);
    }

    [Fact]
    public async Task PostJob_OverTheFamilyLimit_Returns413AndKeepsNothing()
    {
        using var client = factory.CreateClient();
        var storedBefore = factory.StoredFiles().Length;

        using var response = await client.PostAsync(JobsUri, Form(Png(size: (int)ConvertApiFactory.ImageLimitBytes + 1), "big.png", "jpg"), Ct);

        await AssertProblemAsync(response, HttpStatusCode.RequestEntityTooLarge);
        Assert.Equal(storedBefore, factory.StoredFiles().Length);
    }

    [Fact]
    public async Task PostJob_WithoutTargetFormat_Returns400AndKeepsNothing()
    {
        using var client = factory.CreateClient();
        var storedBefore = factory.StoredFiles().Length;

        using var response = await client.PostAsync(JobsUri, Form(Png(size: 50), "photo.png", targetFormat: null), Ct);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest);
        Assert.Equal(storedBefore, factory.StoredFiles().Length);
    }

    [Fact]
    public async Task PostJob_WithoutAFile_Returns400()
    {
        using var client = factory.CreateClient();
        using var form = new MultipartFormDataContent { { new StringContent("jpg"), "targetFormat" } };

        using var response = await client.PostAsync(JobsUri, form, Ct);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PostJob_WithTwoFiles_Returns400AndKeepsNothing()
    {
        using var client = factory.CreateClient();
        var storedBefore = factory.StoredFiles().Length;
        using var form = Form(Png(size: 50), "a.png", "jpg");
        form.Add(new ByteArrayContent(Png(size: 50)), "file", "b.png");

        using var response = await client.PostAsync(JobsUri, form, Ct);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest);
        Assert.Equal(storedBefore, factory.StoredFiles().Length);
    }

    [Fact]
    public async Task PostJob_WithATooLongTargetFormat_Returns400()
    {
        using var client = factory.CreateClient();

        using var response = await client.PostAsync(JobsUri, Form(Png(size: 50), "photo.png", new string('a', 200)), Ct);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PostJob_WithTheTargetAfterTheFile_StillValidatesTheConversion()
    {
        using var client = factory.CreateClient();
        var storedBefore = factory.StoredFiles().Length;
        using var form = new MultipartFormDataContent
        {
            { new ByteArrayContent(Png(size: 50)), "file", "photo.png" },
            { new StringContent("mp3"), "targetFormat" },
        };

        using var response = await client.PostAsync(JobsUri, form, Ct);

        await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity);
        Assert.Equal(storedBefore, factory.StoredFiles().Length);
    }

    [Fact]
    public async Task PostJob_WithAnUnknownPart_Returns400AndKeepsNothing()
    {
        using var client = factory.CreateClient();
        var storedBefore = factory.StoredFiles().Length;
        using var form = Form(Png(size: 50), "photo.png", "jpg");
        form.Add(new StringContent("surprise"), "unexpected");

        using var response = await client.PostAsync(JobsUri, form, Ct);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest);
        Assert.Equal(storedBefore, factory.StoredFiles().Length);
    }

    [Fact]
    public async Task PostJob_WithTooManyParts_Returns400()
    {
        using var client = factory.CreateClient();
        using var form = new MultipartFormDataContent();
        for (var i = 0; i < 20; i++)
        {
            form.Add(new StringContent("jpg"), "targetFormat");
        }

        using var response = await client.PostAsync(JobsUri, form, Ct);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PostJob_WithAMalformedMultipartBody_Returns400()
    {
        using var client = factory.CreateClient();
        using var body = new StringContent("--abc\r\nnot a header line\r\n\r\ntruncated");
        body.Headers.ContentType = MediaTypeHeaderValue.Parse("multipart/form-data; boundary=abc");

        using var response = await client.PostAsync(JobsUri, body, Ct);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PostJob_DeclaringABodyLargerThanAnyLimit_Returns413()
    {
        using var client = factory.CreateClient();
        using var form = Form(Png(size: 50), "photo.png", "jpg");
        using var request = new HttpRequestMessage(HttpMethod.Post, JobsUri) { Content = form };
        await form.LoadIntoBufferAsync(Ct);
        form.Headers.ContentLength = long.MaxValue / 2;

        using var response = await client.SendAsync(request, Ct);

        await AssertProblemAsync(response, HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task PostJob_WithoutMultipart_Returns415()
    {
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(JobsUri, new { targetFormat = "jpg" }, Ct);

        await AssertProblemAsync(response, HttpStatusCode.UnsupportedMediaType);
    }

    [Fact]
    public async Task GetJob_ForAnUnknownId_Returns404()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri($"/v1/jobs/{Guid.NewGuid()}", UriKind.Relative), Ct);

        await AssertProblemAsync(response, HttpStatusCode.NotFound);
    }

    private static Uri JobsUri => new("/v1/jobs", UriKind.Relative);

    private static byte[] Png(int size)
    {
        var bytes = new byte[size];
        PngSignature.CopyTo(bytes, 0);
        return bytes;
    }

    private static MultipartFormDataContent Form(byte[] file, string fileName, string? targetFormat, string contentType = "image/png")
    {
        var form = new MultipartFormDataContent();
        if (targetFormat is not null)
        {
            form.Add(new StringContent(targetFormat), "targetFormat");
        }

        var content = new ByteArrayContent(file);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(content, "file", fileName);
        return form;
    }

    private static async Task<JsonElement> ReadJobAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    private static async Task<JsonElement> AssertProblemAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal((int)expected, problem.GetProperty("status").GetInt32());
        return problem;
    }
}
