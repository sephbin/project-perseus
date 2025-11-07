from django.db import models
from django.db.models.signals import pre_save, post_save
from django.dispatch import receiver
from django.contrib.auth.models import User
from core.models import *



@receiver(post_save, sender=Event)
def Event_Post_Save_Handler (sender, instance, **kwargs):
	print("Event_Post_Save_Handler")
	if instance.event_type == "Revit Start Sync":
		source_model = instance.source_model
		user = instance.user
		accessObjects = Through_SourceToUser.objects.filter(source_model=source_model, user_model=user, access="Queue Syncing")

		for accessObject in accessObjects:
			accessObject.access = "Syncing"
			accessObject.save()
	
	if instance.event_type == "Revit End Sync":
		source_model = instance.source_model
		user = instance.user
		Through_SourceToUser.objects.filter(source_model=source_model, user_model=user, access="Syncing").delete()