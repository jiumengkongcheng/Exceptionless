﻿using System;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.Remoting.Contexts;
using System.Threading.Tasks;
using System.Web.Http;
using AutoMapper;
using Exceptionless.Api.Extensions;
using Exceptionless.Core;
using Exceptionless.Core.Authorization;
using Exceptionless.Core.Extensions;
using Exceptionless.Core.Jobs;
using Exceptionless.Core.Utility;
using Exceptionless.Models;
using Microsoft.Owin.Cors;
using Microsoft.Owin.Extensions;
using Microsoft.Owin.Security.OAuth;
using Owin;
using SimpleInjector;
using SimpleInjector.Integration.WebApi;

namespace Exceptionless.Api {
    public static class AppBuilder {
        public static void Build(IAppBuilder app) {
            BuildWithContainer(app, CreateContainer());
        }

        public static void BuildWithContainer(IAppBuilder app, Container container) {
            if (container == null)
                throw new ArgumentNullException("container");

            var config = new HttpConfiguration();
            config.Filters.Add(new HostAuthenticationFilter(OAuthDefaults.AuthenticationType));
            config.Formatters.Remove(config.Formatters.XmlFormatter);
            config.MapHttpAttributeRoutes();

            container.RegisterWebApiFilterProvider(config);
            try {
                container.Verify();
            } catch (Exception ex) {
                var tempEx = ex;
                while (!(tempEx is ReflectionTypeLoadException)) {
                    if (tempEx.InnerException == null)
                        break;
                    tempEx = tempEx.InnerException;
                }

                var typeLoadException = tempEx as ReflectionTypeLoadException;
                if (typeLoadException != null) {
                    foreach (var loaderEx in typeLoadException.LoaderExceptions)
                        Debug.WriteLine(loaderEx.Message);
                }

                Debug.WriteLine(ex.Message);
                throw;
            }
            config.DependencyResolver = new SimpleInjectorWebApiDependencyResolver(container);
            config.EnableSystemDiagnosticsTracing();

            // sample middleware that would be how we would auth an api token
            // maybe we should be using custom OAuthBearerAuthenticationProvider's
            // http://leastprivilege.com/2013/10/31/retrieving-bearer-tokens-from-alternative-locations-in-katanaowin/
            app.Use((context, next) => {
                var token = context.Request.Query.Get("access_token");
                if (String.IsNullOrEmpty(token)) {
                    var authHeader = context.Request.Headers.Get("Authorization");
                    if (!String.IsNullOrEmpty(authHeader)) {
                        var authHeaderVal = AuthenticationHeaderValue.Parse(authHeader);
                        if (authHeaderVal.Scheme.Equals("token", StringComparison.OrdinalIgnoreCase)
                            || authHeaderVal.Scheme.Equals("bearer", StringComparison.OrdinalIgnoreCase))
                            token = authHeaderVal.Parameter;
                    }
                }

                var projectRepository = container.GetInstance<IProjectRepository>();
                if (String.IsNullOrEmpty(token))
                    return next.Invoke();

                var project = projectRepository.GetByApiKey(token);
                if (project == null)
                    return next.Invoke();

                context.Request.User = PrincipalUtility.CreateUser(_userId, new[] { AuthorizationRoles.GlobalAdmin });
                return next.Invoke();
            });
            app.UseStageMarker(PipelineStage.Authenticate);

            app.CreatePerContext<Lazy<User>>("LasyUser", ctx => {
                if (ctx.Request.User == null || ctx.Request.User.Identity == null || !ctx.Request.User.Identity.IsAuthenticated)
                    return null;

                if (!ctx.Request.User.IsUserAuthType())
                    return null;

                return new Lazy<User>(() => {
                    var userRepository = container.GetInstance<IUserRepository>();
                    return userRepository.GetByIdCached(ctx.Request.User.GetUserId());
                });
            });

            app.CreatePerContext<Lazy<Project>>("LasyProject", ctx => {
                if (ctx.Request.User == null || ctx.Request.User.Identity == null || !ctx.Request.User.Identity.IsAuthenticated)
                    return null;

                if (!ctx.Request.User.IsProjectAuthType())
                    return null;

                return new Lazy<Project>(() => {
                    var projectRepository = container.GetInstance<IProjectRepository>();
                    return projectRepository.GetByIdCached(ctx.Request.User.GetProjectId());
                });
            });

            app.UseCors(CorsOptions.AllowAll);
            //app.MapSignalR();
            app.UseWebApi(config);

            Mapper.Initialize(c => c.ConstructServicesUsing(container.GetInstance));

            // TODO: Remove this as it's only for testing.
            EnsureSampleData(container);
            Task.Factory.StartNew(() => container.GetInstance<ProcessEventPostsJob>().Run());
        }

        private static string _userId;
        private static void EnsureSampleData(Container container) {
            var dataHelper = container.GetInstance<DataHelper>();
            var userRepository = container.GetInstance<IUserRepository>();
            var user = userRepository.FirstOrDefault(u => u.EmailAddress == "test@exceptionless.com");
            if (user == null)
                user = userRepository.Add(new User { EmailAddress = "test@exceptionless.com" });
            _userId = user.Id;
            dataHelper.CreateSampleOrganizationAndProject(user.Id);
        }

        public static Container CreateContainer() {
            var container = new Container();
            container.Options.AllowOverridingRegistrations = true;
            container.Options.PropertySelectionBehavior = new InjectAttributePropertySelectionBehavior();

            container.RegisterPackage<Bootstrapper>();

            return container;
        }
    }
}