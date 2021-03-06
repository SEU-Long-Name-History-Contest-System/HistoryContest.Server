using HistoryContest.Server.Data;
using HistoryContest.Server.Extensions;
using HistoryContest.Server.Services;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Rewrite;
using Microsoft.AspNetCore.SpaServices.Webpack;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.PlatformAbstractions;
using Swashbuckle.AspNetCore.Swagger;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace HistoryContest.Server
{
    public class Startup
    {
        public static IConfigurationRoot Configuration { get; set; }
        public static IHostingEnvironment Environment { get; set; }
        private static Timer syncWithDatabaseTimer;

        public Startup(IHostingEnvironment env)
        {
            if (!Program.FromMain)
            {
                env.EnvironmentName = Program.EnvironmentName;
                env.ContentRootPath = Program.ContentRootPath;
                env.WebRootPath = Path.Combine(env.ContentRootPath, "wwwroot");
            }

            Environment = env;
            var builder = new ConfigurationBuilder()
                .SetBasePath(Path.Combine(env.ContentRootPath, env.IsDevelopment() ? "HistoryContest.Server" : ""))
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                .AddJsonFile($"appsettings.{env.EnvironmentName}.json", optional: true)
                .AddEnvironmentVariables();
            Configuration = builder.Build();
        }

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            // Add mvc framework services.
            var mvcBuilder = services.AddMvc(options =>
            {
                //options.Filters.Add(new AutoValidateAntiforgeryTokenAttribute());
            });

            if (Environment.IsDevelopment())
            {
                mvcBuilder.AddRazorOptions(options =>
                {
                    options.ViewLocationExpanders.Clear();
                    options.ViewLocationExpanders.Add(new LocalViewEngine());
                });
            }
            else
            {
                // mvcBuilder.AddMvcOptions(options =>
                // {
                //     options.Filters.Add(new RequireHttpsAttribute());
                // });
            }

            // Bind Contest Setting with Configuration
            services.Configure<ContestSetting>(Configuration.GetSection("Contest"));

            // Add Contest Settings
            services.Configure<ContestSetting>(c => 
            {
                var contest = Configuration.GetSection("Contest");
                c.TestTime = TimeSpan.FromMinutes(int.Parse(contest[nameof(c.TestTime)]));
                c.SchoolScoreSummaryExpireTime = TimeSpan.FromMinutes(int.Parse(contest[nameof(c.SchoolScoreSummaryExpireTime)]));
            });

            // Add Database Services
            services.AddDbContext<ContestContext>(options =>
                options.UseSqlServer(Configuration.GetConnectionStringByDbType("SQL")));

            // Adds a redis in-memory implementation of IDistributedCache.
            services.AddDistributedRedisCache(options =>
            {
                options.InstanceName = "HistoryContest.Redis";
                options.Configuration = Configuration.GetConnectionStringByDbType("Redis");
            });

            // Add redis service
            services.AddScoped<RedisService>();

            // Add Unit of work service
            services.AddScoped<UnitOfWork>();

            services.AddAntiforgery(options =>
            {
                //options.Cookie.Domain = "mydomain.com";
                //options.Cookie.Name = "HistoryContest.AntiForgery";
                options.Cookie.Path = "/";
                options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                options.FormFieldName = "AntiforgeryField";
                options.HeaderName = "X-XSRF-TOKEN";
                options.SuppressXFrameOptionsHeader = false;
            });

            services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
            {
                options.LoginPath = new PathString("/Account/Login/");
                options.LogoutPath = new PathString("/Account/Logout");
                options.AccessDeniedPath = new PathString("/");
                options.Cookie.Name = "HistoryContest.Cookie.Auth";
                options.Cookie.Path = "/";
                options.Cookie.HttpOnly = true;
                options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            });

            services.AddSession(options =>
            {
                options.IdleTimeout = TimeSpan.FromMinutes(30);
                options.Cookie.Name = "HistoryContest.Cookie.Session";
                options.Cookie.Path = "/";
                options.Cookie.HttpOnly = true;
                options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            });

            // Add logging services to application
            services.AddLogging();

            // Register the Swagger generator, defining one or more Swagger documents
            services.AddSwaggerGen(c =>
            {
                c.SwaggerDoc("seu-history-contest", new Info
                {
                    Title = "SEU History Contest API",
                    Version = "v1",
                    Description = "Backend Web API for History Contest System front end.",
                    TermsOfService = "None",
                    Contact = new Contact { Name = "Vigilans", Email = "vigilans@foxmail.com", Url = "http://history-contest.chinacloudsites.cn/wiki" }
                });

                c.DescribeAllEnumsAsStrings();

                //Set the comments path for the swagger json and ui.
                var basePath = PlatformServices.Default.Application.ApplicationBasePath;
                var xmlPath = Path.Combine(basePath, "HistoryContest.Server.xml");
                c.IncludeXmlComments(xmlPath);
            });

            // Add Cross-Origin Requests service
            services.AddCors(o => o.AddPolicy("OpenPolicy", builder =>
            {
                builder.AllowAnyOrigin()
                       .AllowAnyMethod()
                       .AllowAnyHeader();
            }));

        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, ILoggerFactory loggerFactory, UnitOfWork unitOfWork, IAntiforgery antiforgery)
        {
            loggerFactory.AddConsole(Configuration.GetSection("Logging"));
            loggerFactory.AddDebug();

            if (Environment.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
                if (Program.UseWebpack)
                {
                    app.UseWebpackDevMiddleware(new WebpackDevMiddlewareOptions
                    {
                        HotModuleReplacement = true,
                        ProjectPath = Environment.ContentRootPath + "/HistoryContest.Client"
                    });
                }
            }
            else
            {
                app.UseExceptionHandler("/Home/Error");
                // Redirect all http requests to https
                // app.UseRewriter(new RewriteOptions().AddRedirectToHttps());
            }

            // Use Cookie Authentication
            app.UseAuthentication();

            //app.UseCors("OpenPolicy");

            #region Static file routes
            // use wwwroot static files
            DefaultFilesOptions options = new DefaultFilesOptions();
            options.DefaultFileNames.Clear();
            options.DefaultFileNames.Add("login.html");
            options.DefaultFileNames.Add("index.html");
            app.UseDefaultFiles(options);
            app.UseStaticFiles();

            // enable default url rewrite for wiki
            app.UseDefaultFiles(new DefaultFilesOptions()
            {
                FileProvider = new PhysicalFileProvider(Path.Combine(Environment.ContentRootPath, @"HistoryContest.Docs", "wiki")),
                RequestPath = new PathString("/wiki")
            });

            // use wiki static files
            app.UseStaticFiles(new StaticFileOptions()
            {
                FileProvider = new PhysicalFileProvider(Path.Combine(Environment.ContentRootPath, @"HistoryContest.Docs", "wiki")),
                RequestPath = new PathString("/wiki")
            });

            // defense default
            app.UseDefaultFiles(new DefaultFilesOptions()
            {
                FileProvider = new PhysicalFileProvider(Path.Combine(Environment.ContentRootPath, @"HistoryContest.Docs", "defense")),
                RequestPath = new PathString("/defense")
            });

            // use defense static files
            app.UseStaticFiles(new StaticFileOptions()
            {
                FileProvider = new PhysicalFileProvider(Path.Combine(Environment.ContentRootPath, @"HistoryContest.Docs", "defense")),
                RequestPath = new PathString("/defense")
            });
            #endregion


            #region Api document routes
            // Enable middleware to serve generated Swagger as a JSON endpoint.
            app.UseSwagger(c =>
            {
                c.RouteTemplate = "api/{documentname}/swagger.json";
            });

            // Enable middleware to serve swagger-ui (HTML, JS, CSS etc.), specifying the Swagger JSON endpoint.

            app.UseSwaggerUI(c =>
            {
                c.RoutePrefix = "api";
                c.SwaggerEndpoint("/api/seu-history-contest/swagger.json", "SEU History Contest API v1");
            });
            #endregion


            // use sessions
            app.UseSession();

            app.Use(async (context, next) =>
            {
                await context.Session.LoadAsync();
                string path = context.Request.Path.Value;
                if (!path.ToLower().Contains("/api/account/logout"))
                {
                    // This is needed to provide the token generator with the logged in context (if any) 
                    var authInfo = await context.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                    context.User = authInfo.Principal;

                    var tokens = antiforgery.GetAndStoreTokens(context);
                    context.Response.Cookies.Append("XSRF-TOKEN", tokens.RequestToken, new CookieOptions { HttpOnly = false });
                }
                await next.Invoke();
                await context.Session.CommitAsync();
            });

            #region Javascript spa routes
            app.UseMvc(routes =>
            {
                routes.MapRoute(
                    name: "default",
                    template: "{controller=Home}/{action=Index}/{id?}");

                routes.MapSpaFallbackRoute(
                    name: "spa-fallback",
                    defaults: new { controller = "Home", action = "Index" });
            });
            #endregion


            // Seed database
            if (!unitOfWork.DbContext.AllMigrationsApplied())
            {
                unitOfWork.DbContext.Database.Migrate();
                unitOfWork.DbContext.EnsureAllSeeded();
            }

            if (Program.RefreshCache || Environment.IsDevelopment())
            {
                // flush cache
                var endpoints = RedisService.Connection.GetEndPoints();
                RedisService.Connection.GetServer(endpoints.First()).FlushDatabase(1);

                unitOfWork.QuestionRepository.LoadQuestionsToCache();
                unitOfWork.StudentRepository.LoadStudentsToCache();
            }
            if (Program.RegenerateSeed || Environment.IsDevelopment())
            {
                // Load cache
                var questionSeedService = new QuestionSeedService(unitOfWork);
                int scale = unitOfWork.Configuration.QuestionSeedScale;
                var questionSeeds = questionSeedService.CreateNewSeeds(scale);
                unitOfWork.Cache.QuestionSeeds().SetRange(questionSeeds, s => s.ID.ToString());
            }
            if (Environment.IsDevelopment())
            {
                new TestDataService(unitOfWork).SeedTestedStudents();
            }

            syncWithDatabaseTimer = new Timer(async o =>
            {
                try
                {
                    var _services = new ServiceCollection();
                    ConfigureServices(_services);
                    using (var _serviceProvider = _services.BuildServiceProvider())
                    using (UnitOfWork _unitOfWork = _serviceProvider.GetService<UnitOfWork>())
                    {
                        await _unitOfWork.SaveCacheToDataBase();
                        new ExcelExportService(_unitOfWork).UpdateExcelOfSchool();
                    }
                }
                catch(Exception ex)
                {
                    Console.WriteLine(ex.ToString());
                }
                try
                {
                    GC.Collect();
                    syncWithDatabaseTimer.Change((int)TimeSpan.FromMinutes(10).TotalMilliseconds, Timeout.Infinite);
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.ToString());
                }
            }, null, (int)TimeSpan.FromMinutes(10).TotalMilliseconds, Timeout.Infinite);
        }
    }

    internal class LocalViewEngine : IViewLocationExpander
    {
        public void PopulateValues(ViewLocationExpanderContext context)
        {
            context.Values["customviewlocation"] = nameof(LocalViewEngine);
        }

        public IEnumerable<string> ExpandViewLocations(ViewLocationExpanderContext context, IEnumerable<string> viewLocations)
        {
            return new[]
            {
             "~/HistoryContest.Server/Views/{1}/{0}.cshtml",
             "~/HistoryContest.Server/Views/Shared/{0}.cshtml"
            };
        }
    }
}
