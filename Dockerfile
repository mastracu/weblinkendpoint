FROM fsharp
WORKDIR /src


RUN nuget restore

COPY . /src
EXPOSE 8083
