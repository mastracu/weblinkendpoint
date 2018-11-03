#!/bin/bash
imageName=weblinkendpoint
containerName=weblinkendpoint

docker build -t $imageName -f Dockerfile  .

echo Delete old container...
docker rm -f $containerName

echo Run new container...
docker run -d -p 8083:8083 --name $containerName $imageName
