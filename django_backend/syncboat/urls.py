from rest_framework import routers
from django.urls import include, path, re_path
from django.contrib import admin
from . import views


urlpatterns = [
    path(r'<str:souce_model_id>/', views.syncBoat, name="syncBoat"),
    path(r'changeAccess/user/<str:staffID>/<str:projectFileID>/<str:accessType>/', views.changeAccess, name="changeFileAccessUser"),
	path(r'changeAccess/<str:projectFileID>/<str:accessType>/', views.changeAccess, name="changeFileAccess"),
	path(r'getAccessList/<str:souce_model_id>/', views.getAccessList, name="getAccessList"),
	# path(r'syncboat/changeAccess/<str:projectFileID>/<str:accessType>/', views.changeAccess, name="changeFileAccess"),
]