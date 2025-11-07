from django.shortcuts import get_object_or_404, render, redirect
from rest_framework import viewsets
from rest_framework.response import Response
from rest_framework.viewsets import ReadOnlyModelViewSet
from django_filters.rest_framework import DjangoFilterBackend
from django.views.decorators.csrf import csrf_exempt
from django.http import HttpResponse, JsonResponse
from django.contrib.auth.decorators import login_required
import sys, os
from core.models import *
from web_api.serializers import *
from django.db.models import Q


def errorLog(e, log=[]):
	exc_type, exc_obj, exc_tb = sys.exc_info()
	other = sys.exc_info()[0].__name__
	fname = os.path.split(exc_tb.tb_frame.f_code.co_filename)[1]
	errorType = str(exc_type)
	errob = {"isError": True, "error":str(e), "errorType":errorType, "function":fname, "line":exc_tb.tb_lineno, "log":log}
	return errob

@login_required()
def syncBoat(request, souce_model_id):
	log = []
	try:
		user = request.user
		# user = None
		# try:
			# user = getattr(user, str(__package__)+"_oj_profile")
			# print(user)
		# except:
			# modelClass = globals()[str(__package__)+"_oj_profile"]
			# user, created = staff.objects.update_or_create(email = user.email, name="%s %s"%(user.first_name, user.last_name), generalTitle=generalTitle.objects.all().first(), defaults={"user":user})
			# setattr(user, user)
			# user.save()
			# print(user, getattr(user, str(__package__)+"_oj_profile"))
		# print(user)

		projFile = get_object_or_404(Source, id=int(souce_model_id))
		siblings = projFile.project.sources.all().order_by("name")

		updateModel = Event.objects.filter(Q(event_type="syncBoat_queueing") | Q(event_type="Revit End Sync") | Q(event_type="Revit Start Sync"), source_model=projFile).order_by("-updated_at").first()
		if updateModel == None:
			updateModel = Event(event_type = "syncBoat_queueing", source_model=projFile)
			updateModel.save()

		if request.method == "GET":
			syncob = None
			queued = False
			topOfTheQueue = False
			isSyncing = False
			someoneElseSyncing = None
			try:
				syncob = get_object_or_404(Through_SourceToUser, source_model=projFile, user_model=user, access="Syncing")
				isSyncing = True
			except: pass

			try:
				# someoneElseSyncing = get_object_or_404(Through_SourceToUser, ~Q(user_model=user), source_model=projFile, access="Syncing", )
				queueEvents = projFile.accesingProjFile.filter(access="Syncing")
				log.append("someoneElseSyncing queue")
				for qE in queueEvents:
					log.append(str(qE.user_model))
					if qE.user_model != user:
						someoneElseSyncing = qE
			except Exception as e:
				log.append(str(e))
				pass
			loc = 999
			queue = list(Through_SourceToUser.objects.filter(source_model=projFile, access="Reload Latest"))+list(Through_SourceToUser.objects.filter(source_model=projFile, access="Queue Syncing"))
			inQueue = None
			try:
				# inQueue = get_object_or_404(Through_SourceToUser, source_model=projFile, user_model=user, access="Queue Syncing")
				inQueue = get_object_or_404(Through_SourceToUser, source_model=projFile, user_model=user)
				if inQueue:
					queued = True
				if not someoneElseSyncing:

					if queue[0] == inQueue:
						topOfTheQueue = True
				loc  = list(queue).index(inQueue)
			except: pass

			syncEvents = list(Event.objects.filter(event_type="Revit Start Sync", source_model=projFile).order_by("-updated_at")[:10])
			syncEvents.reverse()

			#clean up sync throughs
			if isSyncing:
				throughs = projFile.accesingProjFile.filter(access="Syncing")
				print(throughs)
				for t in throughs:
					if t.user_model != user:
						t.delete()



			context = {
			"log":log,
			# "projectStaff": projectStaff,
			"location": loc,
			"file": projFile,
			"queue": queue,
			"queued": queued,
			"inQueue": inQueue,
			"topOfTheQueue": topOfTheQueue,
			"someoneElseSyncing": someoneElseSyncing,
			"isSyncing": isSyncing,
			"profile": user,
			"syncing": syncob,
			"updateModel": ReadEventSerializer(updateModel).data,
			"siblings": siblings,
			"syncEvents": syncEvents,
			}
			context["context"] = context.copy()
			# return JsonResponse(context)
			return render(request, "syncBoat/syncsync.html", context)
	except Exception as e:
		print(errorLog(e))
		return JsonResponse(errorLog(e))

def changeAccess(request, projectFileID, accessType, staffID=None):
	log = []
	user = request.user
	if staffID:
		ojprof = get_object_or_404(User, id=staffID)
	else:
		# ojprof = getattr(user, str(__package__)+"_oj_profile")
		ojprof = user
	projFile = get_object_or_404(Source, id=projectFileID)
	
	if accessType == "Cancel":
		trackAccess = get_object_or_404(Through_SourceToUser, source_model=projFile, user_model=ojprof)
		trackAccess.delete()
		log.append("Canceled Access")
	elif accessType == "Promote":
		others = Through_SourceToUser.objects.filter(~Q(user_model=ojprof), source_model=projFile, )
		for o in others:
			o.save()
		log.append("Promoted Access")
	elif accessType == "Demote":
		trackAccess = Through_SourceToUser.objects.update_or_create(user_model=ojprof, source_model=projFile)
		log.append("Demoted Access")
	else:
		trackAccess = Through_SourceToUser.objects.update_or_create(source_model=projFile, user_model=ojprof, defaults={"access":accessType})
		log.append("Changed Access")
	
	return JsonResponse({"isError":False, "log":log})

def getAccessList(request, souce_model_id):
	try:
		projFile = get_object_or_404(Source, id=int(souce_model_id))
		updateModel = Event.objects.filter(Q(event_type="syncBoat_queueing") | Q(event_type="Revit End Sync") | Q(event_type="Revit Start Sync"), source_model=projFile).order_by("-updated_at").first()
		data = ReadEventSerializer(updateModel).data
		return JsonResponse(data)
	except:
		return JsonResponse({"updated":"None"})
		pass