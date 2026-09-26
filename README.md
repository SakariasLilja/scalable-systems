# Project for Aalto course CS-E4780 Scalable Systems and Data Management D

## Table of contents:
1. [Local setup requirements](#local-setup-requirements)

## Local setup requirements
The project has been built in Linux utilising Docker and Docker compose for container management. If you prefer to use other container management software (e.g. Rancher or Podman), you may need to tweak some aspects in the project manually.
With the Docker engine running, you can launch the application with the command:  
`docker compose up --build`

> The first time you launch the application it may take longer as it needs to download the necessary container images. 

### Environment variables
This project utilises a `.env` file to store environment variables. To ensure no API-keys or secrets are published on accident, the `.env` file has been omitted from the GitHub repository.

> Ensure the file is named exactly `.env` and that it is located in the project root.

To run the application, you need to add it manually with the following contents.
```
POSTGRES_USER=postgres
POSTGRES_PASSWORD=password
POSTGRES_DB=db
```