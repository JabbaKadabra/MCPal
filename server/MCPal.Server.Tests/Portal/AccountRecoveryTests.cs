using System.Net;
using System.Net.Http.Json;
using MCPal.Server.Tests.Infrastructure;

namespace MCPal.Server.Tests.Portal;

/// <summary>Email confirmation and password reset.</summary>
[TestFixture]
internal sealed class AccountRecoveryTests
{
    private const string Email = "admin@acme.example";

    private static CancellationToken Ct => TestContext.CurrentContext.CancellationToken;

    private static async Task<HttpResponseMessage> ConfirmAsync(PortalClient portal, SentEmail mail) =>
        await portal.PostAsync("/api/portal/auth/confirm-email", new { userId = mail.QueryValue("userId"), token = mail.QueryValue("token") }, Ct);

    private static async Task<HttpResponseMessage> LoginAsync(PortalClient portal, string password) =>
        await portal.PostAsync("/api/portal/auth/login", new { email = Email, password }, Ct);

    /// <summary>Signs up and returns the factory state after the first mail arrived.</summary>
    private static async Task SignupAsync(ServerWebApplicationFactory factory)
    {
        using var portal = new PortalClient(factory);
        (await portal.SignupAsync("Acme", Email, Ct)).Dispose();
    }

    [Test]
    public async Task Signup_SendsConfirmationMailWithLinkToPublicUrlAndStaysSignedIn()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct, settings: new() { ["Mcpal:PublicUrl"] = "https://mcpal.example.com" });
        using var portal = new PortalClient(factory);

        using var signup = await portal.SignupAsync("Acme", Email, Ct);
        using var me = await portal.GetAsync("/api/portal/auth/me", Ct);

        var mail = factory.Emails.To(Email).Should().ContainSingle().Which;
        mail.Link().GetLeftPart(UriPartial.Path).Should().Be("https://mcpal.example.com/confirm-email");
        mail.QueryValue("userId").Should().NotBeNullOrEmpty();
        mail.QueryValue("token").Should().NotBeNullOrEmpty();
        mail.HtmlBody.Should().Contain(mail.Link().ToString().Replace("&", "&amp;", StringComparison.Ordinal));
        me.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public async Task Login_EmailNotConfirmedAndCorrectPassword_Returns403WithCode()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        await SignupAsync(factory);
        using var browser = new PortalClient(factory);

        using var response = await LoginAsync(browser, PortalClient.Password);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await PortalClient.JsonAsync(response, Ct)).GetProperty("error").GetString().Should().Be("email_not_confirmed");
    }

    [Test]
    public async Task Login_EmailNotConfirmedAndWrongPassword_LooksLikeAnyWrongPassword()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        await SignupAsync(factory);
        using var browser = new PortalClient(factory);

        using var response = await LoginAsync(browser, "wrong-password-1");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await PortalClient.JsonAsync(response, Ct)).TryGetProperty("error", out _).Should().BeFalse();
    }

    [Test]
    public async Task ConfirmEmail_LinkFromMail_ConfirmsAndAllowsLogin()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        await SignupAsync(factory);
        using var browser = new PortalClient(factory);

        using var confirm = await ConfirmAsync(browser, factory.Emails.To(Email).Single());
        using var login = await LoginAsync(browser, PortalClient.Password);

        confirm.StatusCode.Should().Be(HttpStatusCode.NoContent);
        login.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public async Task ConfirmEmail_TamperedTokenOrUnknownUser_Returns400WithSameMessage()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        await SignupAsync(factory);
        var mail = factory.Emails.To(Email).Single();
        using var browser = new PortalClient(factory);

        using var badToken = await browser.PostAsync("/api/portal/auth/confirm-email", new { userId = mail.QueryValue("userId"), token = "AAAA" }, Ct);
        using var badUser = await browser.PostAsync("/api/portal/auth/confirm-email", new { userId = Guid.NewGuid().ToString(), token = mail.QueryValue("token") }, Ct);
        using var noBody = await browser.PostAsync("/api/portal/auth/confirm-email", new { }, Ct);

        badToken.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        badUser.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        noBody.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await badToken.Content.ReadAsStringAsync(Ct)).Should().Be(await badUser.Content.ReadAsStringAsync(Ct));
    }

    [Test]
    public async Task ResendConfirmation_UnconfirmedUser_SendsNewMailAndReturns204()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        await SignupAsync(factory);
        using var browser = new PortalClient(factory);

        using var response = await browser.PostAsync("/api/portal/auth/resend-confirmation", new { email = Email }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var mails = factory.Emails.To(Email);
        mails.Should().HaveCount(2);
        using var confirm = await ConfirmAsync(browser, mails[1]);
        confirm.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Test]
    public async Task ResendConfirmation_UnknownOrConfirmedEmail_Returns204WithoutMail()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        await SignupAsync(factory);
        using var browser = new PortalClient(factory);
        (await ConfirmAsync(browser, factory.Emails.To(Email).Single())).Dispose();
        var before = factory.Emails.Sent.Count;

        using var unknown = await browser.PostAsync("/api/portal/auth/resend-confirmation", new { email = "nobody@acme.example" }, Ct);
        using var confirmed = await browser.PostAsync("/api/portal/auth/resend-confirmation", new { email = Email }, Ct);

        unknown.StatusCode.Should().Be(HttpStatusCode.NoContent);
        confirmed.StatusCode.Should().Be(HttpStatusCode.NoContent);
        factory.Emails.Sent.Should().HaveCount(before);
    }

    [Test]
    public async Task ForgotPassword_KnownAndUnknownEmail_LookIdenticalButOnlyKnownGetsMail()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        await SignupAsync(factory);
        using var browser = new PortalClient(factory);
        var before = factory.Emails.Sent.Count;

        using var known = await browser.PostAsync("/api/portal/auth/forgot-password", new { email = Email }, Ct);
        using var unknown = await browser.PostAsync("/api/portal/auth/forgot-password", new { email = "nobody@acme.example" }, Ct);
        using var malformed = await browser.PostAsync("/api/portal/auth/forgot-password", new { email = "not an email" }, Ct);

        known.StatusCode.Should().Be(HttpStatusCode.NoContent);
        unknown.StatusCode.Should().Be(HttpStatusCode.NoContent);
        malformed.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await known.Content.ReadAsStringAsync(Ct)).Should().Be(await unknown.Content.ReadAsStringAsync(Ct));
        factory.Emails.Sent.Skip(before).Should().ContainSingle().Which.To.Should().Be(Email);
    }

    [Test]
    public async Task ResetPassword_LinkFromMail_ChangesPasswordOnceAndConfirmsTheEmail()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        await SignupAsync(factory);
        using var browser = new PortalClient(factory);
        (await browser.PostAsync("/api/portal/auth/forgot-password", new { email = Email }, Ct)).Dispose();
        var mail = factory.Emails.To(Email)[^1];
        mail.Link().GetLeftPart(UriPartial.Path).Should().EndWith("/reset-password");
        var reset = new { email = Email, token = mail.QueryValue("token"), newPassword = "another-long-password" };

        using var first = await browser.PostAsync("/api/portal/auth/reset-password", reset, Ct);
        using var second = await browser.PostAsync("/api/portal/auth/reset-password", reset with { newPassword = "yet-another-password" }, Ct);
        using var oldPassword = await LoginAsync(browser, PortalClient.Password);
        using var newPassword = await LoginAsync(browser, "another-long-password");

        first.StatusCode.Should().Be(HttpStatusCode.NoContent);
        second.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        oldPassword.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        newPassword.StatusCode.Should().Be(HttpStatusCode.OK, "the reset link proves the mailbox, so the account counts as confirmed");
    }

    [Test]
    public async Task ResetPassword_WeakPassword_Returns400WithReasonsAndKeepsOldPassword()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        await SignupAsync(factory);
        using var browser = new PortalClient(factory);
        (await browser.PostAsync("/api/portal/auth/forgot-password", new { email = Email }, Ct)).Dispose();
        var mail = factory.Emails.To(Email)[^1];

        using var response = await browser.PostAsync("/api/portal/auth/reset-password", new { email = Email, token = mail.QueryValue("token"), newPassword = "short" }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await PortalClient.JsonAsync(response, Ct)).GetProperty("errors").GetArrayLength().Should().BeGreaterThan(0);
    }

    [Test]
    public async Task ResetPassword_WrongEmailOrGarbageToken_Returns400WithSameMessage()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        await SignupAsync(factory);
        using var browser = new PortalClient(factory);
        (await browser.PostAsync("/api/portal/auth/forgot-password", new { email = Email }, Ct)).Dispose();
        var mail = factory.Emails.To(Email)[^1];

        using var wrongEmail = await browser.PostAsync("/api/portal/auth/reset-password", new { email = "nobody@acme.example", token = mail.QueryValue("token"), newPassword = "another-long-password" }, Ct);
        using var garbage = await browser.PostAsync("/api/portal/auth/reset-password", new { email = Email, token = "!!!", newPassword = "another-long-password" }, Ct);

        wrongEmail.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        garbage.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await wrongEmail.Content.ReadAsStringAsync(Ct)).Should().Be(await garbage.Content.ReadAsStringAsync(Ct));
    }

    [Test]
    public async Task ResetPassword_TokenOfOtherUser_DoesNotChangeThisUsersPassword()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        await SignupAsync(factory);
        using (var other = new PortalClient(factory))
        {
            (await other.SignupAsync("Globex", "boss@globex.example", Ct)).Dispose();
        }

        using var browser = new PortalClient(factory);
        (await browser.PostAsync("/api/portal/auth/forgot-password", new { email = "boss@globex.example" }, Ct)).Dispose();
        var globexToken = factory.Emails.To("boss@globex.example")[^1].QueryValue("token");

        using var response = await browser.PostAsync("/api/portal/auth/reset-password", new { email = Email, token = globexToken, newPassword = "another-long-password" }, Ct);
        using var stillOld = await LoginAsync(new PortalClient(factory), PortalClient.Password);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        stillOld.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized, "the password of the account is unchanged (it may only be unconfirmed)");
    }

    [Test]
    public async Task ForgotPassword_TooManyRequests_Returns429()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        using var http = factory.CreateClient();
        using var csrf = await http.GetAsync("/api/portal/csrf", Ct);
        var token = (await PortalClient.JsonAsync(csrf, Ct)).GetProperty("token").GetString() ?? string.Empty;
        var statuses = new List<HttpStatusCode>();

        for (var i = 0; i < 130; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/portal/auth/forgot-password") { Content = JsonContent.Create(new { email = $"user{i}@acme.example" }) };
            request.Headers.Add("X-CSRF-TOKEN", token);
            using var response = await http.SendAsync(request, Ct);
            statuses.Add(response.StatusCode);
        }

        statuses.Should().Contain(HttpStatusCode.TooManyRequests);
        statuses[0].Should().Be(HttpStatusCode.NoContent);
    }

    [Test]
    public async Task ForgotPassword_WithoutAntiforgeryHeader_Returns400()
    {
        await using var factory = await ServerWebApplicationFactory.CreateAsync(Ct);
        using var browser = new PortalClient(factory);

        using var response = await browser.PostAsync("/api/portal/auth/forgot-password", new { email = Email }, Ct, withCsrf: false);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
