from django.db import models
from django.db.models.signals import pre_save
from django.dispatch import receiver

class Source(models.Model):
    unique_id = models.CharField(max_length=128, unique=True)
    name = models.TextField(max_length=255, blank=True)
    medium = models.TextField(max_length=255, blank=True)


class Element(models.Model):
    element_id = models.TextField(max_length=255)
    unique_id = models.CharField(max_length=128, unique=True)
    name = models.TextField(max_length=255, blank=True)
    created_at = models.DateTimeField(auto_now_add=True)
    updated_at = models.DateTimeField(auto_now=True)
    last_edited_by = models.TextField(max_length=255, blank=True)
    source_model = models.ForeignKey('Source', on_delete=models.CASCADE, related_name='elements', blank=True, null=True)
    source_state = models.TextField(max_length=255, blank=True, null=True)

    @property
    def parameterList(self):
        params = list(map(lambda x: x.name+": "+x.value, self.parameters.all()))
        params = "\n".join(params)
        return params
    @property 
    def parameter_dict(self):
        outDict = {} 
        for param in self.parameters.all():
            value = param.value
            # print(param.value_type, param.value)
            if param.value_type == "ElementId":
                try: value = Element.objects.get(element_id=value).name
                except Exception as e:
                    # print(e)
                    pass
            outDict[param.name] = value
        return outDict

    


class Parameter(models.Model):
    name = models.CharField(max_length=255, blank=True, null=True)
    value = models.TextField(max_length=255, blank=True, null=True)
    value_type = models.TextField(max_length=255, blank=True, null=True)
    element = models.ForeignKey('Element', on_delete=models.CASCADE, related_name='parameters')

    class Meta:
        unique_together = ('element', 'name')


class Event(models.Model):
    name = models.CharField(max_length=255, blank=True, null=True)
    created_at = models.DateTimeField(auto_now_add=True)
    description = models.TextField(blank=True, null=True)




@receiver(pre_save, sender=Parameter)
def parameter_changed_handler(sender, instance, **kwargs):
    print('parameter_changed_handler')
    print(instance.pk)
    if instance.pk:  # Only for existing instances, not new ones
        try:
            old_instance = sender.objects.get(pk=instance.pk)
            print(old_instance)
            if old_instance.name == "Area":
                try:
                    old_Area = float(old_instance.value)
                    new_Area = float(instance.value)
                    print(old_Area, new_Area)
                    print(instance.element.__dict__)
                    if old_Area > 0.0:
                        if new_Area == 0.0:
                            instanceDict = instance.element.__dict__
                            description = "{element_id} {name}: Room Area Set to 0 by {last_edited_by}".format(**instanceDict)
                            Event(name="Room Drop", description=description).save()
                except Exception as e: print(e)

                    
        except Exception as e:
            print(e)
            pass  # Handle cases where the instance might not exist (e.g., race conditions)