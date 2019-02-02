#!/bin/bash
# $1 = weblinkendpoint
echo "building container: docker build -t $1 ."
docker build -t $1 .
echo "stopping all containers"
docker stop $(docker ps -a -q)
echo "removing all untagged images"
docker images --no-trunc | grep '<none>' | awk '{ print $3 }'  | xargs -r docker rmi
echo "starting container: docker run -d -p8083:8083 --rm $1"
docker run -d -p8083:8083 --rm $1