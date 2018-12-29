FROM fsharp
WORKDIR /src


COPY . /src
RUN nuget restore && xbuild
EXPOSE 8083