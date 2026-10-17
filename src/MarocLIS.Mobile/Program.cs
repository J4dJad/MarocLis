using MarocLIS.Mobile.Services;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

// MarocLIS.Mobile — the biologist's phone application (« Validation à distance », phase 2).
//
// Served by MarocLIS.Api from the same origin, so every call goes to the address the page came
// from: the session cookie is sent by the browser on its own (HttpOnly, SameSite=Strict — no
// script reads it, this one included), and there is no API address to configure on the phone.
namespace MarocLIS.Mobile;

/// <summary>
/// The entry point, named rather than top-level: a top-level Program would be the global
/// <c>Program</c>, the name the API's entry point already has for the tests' WebApplicationFactory,
/// which reference both.
/// </summary>
internal static class MobileProgram
{
    private static async Task Main(string[] args)
    {
        var builder = WebAssemblyHostBuilder.CreateDefault(args);
        builder.RootComponents.Add<App>("#app");
        builder.RootComponents.Add<HeadOutlet>("head::after");

        builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
        builder.Services.AddMobileServices();

        await builder.Build().RunAsync();
    }
}
