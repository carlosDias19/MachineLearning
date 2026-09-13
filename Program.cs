using EstudaBot.Data;
using EstudaBot.Application.Chatbot;
using EstudaBot.Infrastructure.Knowledge;
using EstudaBot.Infrastructure.MachineLearning;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var dataDirectory = Path.Combine(builder.Environment.ContentRootPath, "Data");
Directory.CreateDirectory(dataDirectory);

// Add services to the container.
builder.Services.AddRazorPages();
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromHours(2);
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
});
builder.Services.AddDbContext<ChatDbContext>(options =>
    options.UseSqlite($"Data Source={Path.Combine(dataDirectory, "estudabot.db")}"));
builder.Services.AddSingleton<IntentClassifier>();
builder.Services.AddHttpClient<KnowledgeSearchService>(client =>
{
    client.BaseAddress = new Uri("https://pt.wikipedia.org/");
    client.Timeout = TimeSpan.FromSeconds(8);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("EstudaBot/1.0 (assistente de estudos)");
});
builder.Services.AddScoped<StudyChatbot>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    scope.ServiceProvider.GetRequiredService<ChatDbContext>().Database.EnsureCreated();
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseSession();
app.UseRouting();

app.UseAuthorization();

app.MapStaticAssets();
app.MapRazorPages()
   .WithStaticAssets();

app.Run();
