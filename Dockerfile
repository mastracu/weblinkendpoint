FROM fsharp
WORKDIR /src


COPY . /src
RUN nuget restore
EXPOSE 8083
