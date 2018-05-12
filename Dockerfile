FROM microsoft/dotnet:2.1-sdk AS build  
WORKDIR /app

COPY ./*.sln ./NuGet.Config ./
COPY ./build/*.props ./build/

# Copy the main source project files
COPY src/*/*.csproj ./
RUN for file in $(ls *.csproj); do mkdir -p src/${file%.*}/ && mv $file src/${file%.*}/; done

# Copy the individual jobs (temporary)
COPY src/Jobs/*/*.csproj ./
RUN for file in $(ls *.csproj); do mkdir -p src/Jobs/${file%.*}/ && mv $file src/Jobs/${file%.*}/; done

# Copy the test project files
COPY tests/*/*.csproj ./
RUN for file in $(ls *.csproj); do mkdir -p tests/${file%.*}/ && mv $file tests/${file%.*}/; done

RUN dotnet restore

# Copy everything else and build app
COPY . .
RUN dotnet build

# test

FROM build AS test
WORKDIR /app/tests/Exceptionless.Tests
#RUN dotnet test

# job-publish

FROM build AS job-publish
WORKDIR /app/src/Exceptionless.Job
RUN dotnet publish -c Release -o out

# job

FROM microsoft/dotnet:2.1-runtime AS job
WORKDIR /app
COPY --from=job-publish /app/src/Exceptionless.Job/out ./
ENTRYPOINT [ "dotnet", "Exceptionless.Job.dll" ]


# web-publish

FROM build AS web-publish
WORKDIR /app/src/Exceptionless.Web
RUN dotnet publish -c Release -o out

# web

FROM microsoft/dotnet:2.1-aspnetcore-runtime AS web
WORKDIR /app
COPY --from=web-publish /app/src/Exceptionless.Web/out ./
ENTRYPOINT [ "dotnet", "Exceptionless.Web.dll" ]