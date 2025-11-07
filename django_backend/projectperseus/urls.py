"""
URL configuration for projectperseus project.

The `urlpatterns` list routes URLs to views. For more information please see:
    https://docs.djangoproject.com/en/4.2/topics/http/urls/
Examples:
Function views
    1. Add an import:  from my_app import views
    2. Add a URL to urlpatterns:  path('', views.home, name='home')
Class-based views
    1. Add an import:  from other_app.views import Home
    2. Add a URL to urlpatterns:  path('', Home.as_view(), name='home')
Including another URLconf
    1. Import the include() function: from django.urls import include, path
    2. Add a URL to urlpatterns:  path('blog/', include('blog.urls'))
"""
from django.contrib import admin
from django.urls import path, include
from rest_framework import routers
from django.contrib.auth import views as auth_views

from web_api import views

router = routers.DefaultRouter()
router.register(r'elements', views.ElementDeltaSubmissionView)
router.register(r'parameters', views.ParameterReadView)
router.register(r'sources', views.SourceReadView)

urlpatterns = [
    path(r'syncboat/', include('syncboat.urls')),
    path('getstate/<str:source>', views.getLatestState),
    path('presync/<str:source>', views.preSync),
    path('rapi/', include(router.urls)),
    path('stateupdate/', views.stateUpdate),
    path('api-auth/', include('rest_framework.urls', namespace='rest_framework')),
    path('users/', include('users.urls')),
    # path('accounts/', include('django.contrib.auth.urls')),
    
]+[
path('password_reset/',
         auth_views.PasswordResetView.as_view(template_name='users/password_reset.html'),
         name='password_reset'),
    path('password_reset_done/',
         auth_views.PasswordResetDoneView.as_view(template_name='users/password_reset_done.html'),
         name='password_reset_done'),
    path('reset/<uidb64>/<token>/',
         auth_views.PasswordResetConfirmView.as_view(template_name='users/password_reset_confirm.html'),
         name='password_reset_confirm'),
    path('reset/done/',
         auth_views.PasswordResetCompleteView.as_view(template_name='users/password_reset_complete.html'),
         name='password_reset_complete'),
    ]+[
    path('', admin.site.urls),
    ]
