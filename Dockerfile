FROM fsharp
WORKDIR /src


COPY . /src
COPY ./firmware /src
RUN nuget restore && xbuild
EXPOSE 8083
ENTRYPOINT ["mono", "WebSocket.exe"]